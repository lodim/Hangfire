﻿// This file is part of Hangfire. Copyright © 2014 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Hangfire.Common;
using Hangfire.Logging;
using Hangfire.States;
using Hangfire.Storage;
using Newtonsoft.Json;

namespace Hangfire
{
    /// <summary>
    /// Represents a job filter that performs <i>automatic retries</i> for 
    /// background jobs whose processing was failed due to an exception, with 
    /// a limited number of attempts.
    /// </summary>
    /// 
    /// <remarks>
    /// <para>Filter is added to the global <see cref="GlobalJobFilters.Filters"/> 
    /// collection by default. Intervals between attempts are based on increasing 
    /// exponential back-off multiplier in seconds.</para>
    /// 
    /// <para>This filter works in a <i>state election</i> phase by changing the
    /// candidate state from <see cref="FailedState"/> to the <see cref="ScheduledState"/>
    /// when another retry should be attempted, or other state based on the value
    /// of the <see cref="OnAttemptsExceeded"/> property when attempts exceeded.
    /// </para>
    /// </remarks>
    /// 
    /// <example>
    /// <h3>Disabling Automatic Retries</h3>
    /// <para>The following example shows how to disable automatic retries for
    /// a specific job method by applying an attribute to a method.</para>
    /// 
    /// <note>Even if you disable <see cref="AutomaticRetryAttribute"/> filter, 
    /// your background jobs can still be executed several times, due to re-queue 
    /// on shutdown and other compensation logic that guarantees the <i>at least
    /// once</i> processing.</note>
    /// 
    /// <code lang="cs" source="..\Samples\AutomaticRetry.cs" region="Disable Retries" />
    /// 
    /// <h3>Overriding Defaults</h3>
    /// <para>The following example shows how to override the default number of
    /// retry attempts for all of the background jobs by modifying the global
    /// <see cref="GlobalJobFilters.Filters"/> collection.</para>
    /// 
    /// <code lang="cs" source="..\Samples\AutomaticRetry.cs" region="Override Default" />
    /// 
    /// <h3>Specifying Attempts Exceeded Action</h3>
    /// <para>The following example shows how to ignore a background job when
    /// number of retry attempts exceed using the <see cref="OnAttemptsExceeded"/>
    /// property.</para>
    /// 
    /// <note type="tip">Choose <see cref="AttemptsExceededAction.Delete"/> action 
    /// when you aren't interested in processing background job that failed several
    /// times.</note>
    /// 
    /// <code lang="cs" source="..\Samples\AutomaticRetry.cs" region="Attempts Exceeded" />
    /// </example>
    /// 
    /// <threadsafety static="true" instance="true" />
    public sealed class AutomaticRetryAttribute : JobFilterAttribute, IElectStateFilter, IApplyStateFilter
    {
        /// <summary>
        /// Represents the default number of retry attempts. This field is read-only.
        /// </summary>
        /// <remarks>
        /// The value of this field is <c>10</c>.
        /// </remarks>
        public static readonly int DefaultRetryAttempts = 10;

        private static readonly Func<long, int> DefaultDelayInSecondsByAttemptFunc = attempt =>
        {
            var random = new Random();
            return (int)Math.Round(
                Math.Pow(attempt - 1, 4) + 15 + random.Next(30) * attempt);
        };
        
        private readonly ILog _logger = LogProvider.For<AutomaticRetryAttribute>();
        
        private readonly object _lockObject = new object();
        private int _attempts;
        private int[]  _delaysInSeconds;
        private Func<long, int> _delayInSecondsByAttemptFunc;
        private AttemptsExceededAction _onAttemptsExceeded;
        private bool _logEvents;
        private Type[] _onlyOn;
        private Type[] _exceptOn;

        /// <summary>
        /// Initializes a new instance of the <see cref="AutomaticRetryAttribute"/>
        /// class with <see cref="DefaultRetryAttempts"/> number.
        /// </summary>
        public AutomaticRetryAttribute()
        {
            Attempts = DefaultRetryAttempts;
            DelayInSecondsByAttemptFunc = DefaultDelayInSecondsByAttemptFunc;
            LogEvents = true;
            OnAttemptsExceeded = AttemptsExceededAction.Fail;
            Order = 20;
        }

        /// <summary>
        /// Gets or sets the maximum number of automatic retry attempts.
        /// </summary>
        /// <value>Any non-negative number.</value>
        /// <exception cref="ArgumentOutOfRangeException">The value in a set operation is less than zero.</exception>
        public int Attempts
        {
            get { lock (_lockObject) { return _attempts; } }
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), @"Attempts value must be equal or greater than zero.");
                }

                lock (_lockObject)
                {
                    _attempts = value;
                }
            }
        }

        /// <summary>
        /// Gets or sets the delays between attempts.
        /// </summary>
        /// <value>An array of non-negative numbers.</value>
        /// <exception cref="ArgumentNullException">The value in a set operation is null.</exception>
        /// <exception cref="ArgumentException">The value contain one or more negative numbers.</exception>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int[] DelaysInSeconds
        {
            get { lock (_lockObject) { return _delaysInSeconds; } }
            set
            {
                if (value != null)
                {
                    if (value.Length == 0) throw new ArgumentNullException(nameof(value));
                    if (value.Any(static delay => delay < 0))
                        throw new ArgumentException(
                            $@"{nameof(DelaysInSeconds)} value must be an array of non-negative numbers.",
                            nameof(value));
                }

                lock (_lockObject) { _delaysInSeconds = value; }
            }
        }

        /// <summary>
        /// Gets or sets a function using to get a delay by an attempt number.
        /// </summary>
        /// <exception cref="ArgumentNullException">The value in a set operation is null.</exception>
        [JsonIgnore]
        public Func<long, int> DelayInSecondsByAttemptFunc
        {
            get { lock (_lockObject) { return _delayInSecondsByAttemptFunc;} }
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                lock (_lockObject) { _delayInSecondsByAttemptFunc = value; }
            }
        }

        /// <summary>
        /// Gets or sets a candidate state for a background job that 
        /// will be chosen when number of retry attempts exceeded.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public AttemptsExceededAction OnAttemptsExceeded
        {
            get { lock (_lockObject) { return _onAttemptsExceeded; } }
            set { lock (_lockObject) { _onAttemptsExceeded = value; } }
        }

        /// <summary>
        /// Gets or sets whether to produce log messages on retry attempts.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        [DefaultValue(true)]
        public bool LogEvents
        {
            get { lock (_lockObject) { return _logEvents; } }
            set { lock (_lockObject) { _logEvents = value; } }
        }

        /// <summary>
        /// Gets a sets an array of exception types that will be used to determine whether
        /// automatic retry logic should be attempted to run. By default it will be run on
        /// any exception, but this property allow to reduce it only to some specific
        /// exception types and their subtypes.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Type[] OnlyOn
        {
            get { lock (_lockObject) { return _onlyOn; } }
            set { lock (_lockObject) { _onlyOn = value; } }
        }

        /// <summary>
        /// Gets or sets the array of exception types on which the automatic retry mechanism
        /// should not be applied.
        /// </summary>
        /// <value>
        /// An array of <see cref="System.Type"/> objects representing the exception types to
        /// be excluded from automatic retries.
        /// </value>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Type[] ExceptOn
        {
            get { lock (_lockObject) { return _exceptOn; } }
            set { lock (_lockObject) { _exceptOn = value; } }
        }

        /// <inheritdoc />
        public void OnStateElection(ElectStateContext context)
        {
            var failedState = context.CandidateState as FailedState;
            if (failedState == null)
            {
                // This filter accepts only failed job state.
                return;
            }

            if (_onlyOn != null && _onlyOn.Length > 0)
            {
                var exceptionType = failedState.Exception.GetType();
                var satisfied = false;

                foreach (var onlyOn in _onlyOn)
                {
                    if (onlyOn.GetTypeInfo().IsAssignableFrom(exceptionType.GetTypeInfo()))
                    {
                        satisfied = true;
                        break;
                    }
                }

                if (!satisfied) return;
            }

            if (_exceptOn != null && _exceptOn.Length > 0)
            {
                var exceptionType = failedState.Exception.GetType();
                var satisfied = true;

                foreach (var exceptOn in _exceptOn)
                {
                    if (exceptOn.GetTypeInfo().IsAssignableFrom(exceptionType.GetTypeInfo()))
                    {
                        satisfied = false;
                        break;
                    }
                }

                if (!satisfied) return;
            }

            var retryAttempt = context.GetJobParameter<int>("RetryCount", allowStale: true) + 1;

            if (retryAttempt <= Attempts)
            {
                ScheduleAgainLater(context, retryAttempt, failedState);
            }
            else if (retryAttempt > Attempts && OnAttemptsExceeded == AttemptsExceededAction.Delete)
            {
                TransitionToDeleted(context, failedState);
            }
            else
            {
                if (LogEvents)
                {
                    _logger.ErrorException(
                        $"Failed to process the job '{context.BackgroundJob.Id}': an exception occurred.",
                        failedState.Exception);
                }
            }
        }

        /// <inheritdoc />
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context.NewState is ScheduledState &&
                context.NewState.Reason != null &&
                context.NewState.Reason.StartsWith("Retry attempt", StringComparison.OrdinalIgnoreCase))
            {
                transaction.AddToSet("retries", context.BackgroundJob.Id);
            }
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (ScheduledState.StateName.Equals(context.OldStateName, StringComparison.OrdinalIgnoreCase) ||
                FailedState.StateName.Equals(context.OldStateName, StringComparison.OrdinalIgnoreCase))
            {
                transaction.RemoveFromSet("retries", context.BackgroundJob.Id);
            }
        }

        /// <summary>
        /// Schedules the job to run again later. See <see cref="DelayInSecondsByAttemptFunc"/>.
        /// </summary>
        /// <param name="context">The state context.</param>
        /// <param name="retryAttempt">The count of retry attempts made so far.</param>
        /// <param name="failedState">Object which contains details about the current failed state.</param>
        private void ScheduleAgainLater(ElectStateContext context, int retryAttempt, FailedState failedState)
        {
            context.SetJobParameter("RetryCount", retryAttempt);

            int delayInSeconds;
            
            if (_delaysInSeconds != null)
            {
                delayInSeconds = retryAttempt <= _delaysInSeconds.Length
                    ? _delaysInSeconds[retryAttempt - 1]
                    : _delaysInSeconds.Last();
            }
            else
            {
                delayInSeconds = DelayInSecondsByAttemptFunc(retryAttempt);                
            }

            var delay = TimeSpan.FromSeconds(delayInSeconds);          

            const int maxMessageLength = 50;
            var exceptionMessage = failedState.Exception.Message.Length > maxMessageLength
                ? failedState.Exception.Message.Substring(0, maxMessageLength - 1) + "…" 
                : failedState.Exception.Message;

            // If attempt number is less than max attempts, we should
            // schedule the job to run again later.
            
            var reason = $"Retry attempt {retryAttempt} of {Attempts}: {exceptionMessage}";

            context.CandidateState = delay == TimeSpan.Zero
                ? (IState)new EnqueuedState { Reason = reason }
                : new ScheduledState(delay) { Reason = reason };

            if (LogEvents)
            {
                _logger.WarnException(
                    $"Failed to process the job '{context.BackgroundJob.Id}': an exception occurred. Retry attempt {retryAttempt} of {Attempts} will be performed in {delay}.",
                    failedState.Exception);
            }
        }

        /// <summary>
        /// Transition the candidate state to the deleted state.
        /// </summary>
        /// <param name="context">The state context.</param>
        /// <param name="failedState">Object which contains details about the current failed state.</param>
        private void TransitionToDeleted(ElectStateContext context, FailedState failedState)
        {
            context.CandidateState = new DeletedState(new ExceptionInfo(failedState.Exception))
            {
                Reason = Attempts > 0
                    ? "Exceeded the maximum number of retry attempts."
                    : "Retries were disabled for this job."
            };

            if (LogEvents)
            {
                _logger.WarnException(
                    $"Failed to process the job '{context.BackgroundJob.Id}': an exception occured. Job was automatically deleted because the retry attempt count exceeded {Attempts}.",
                    failedState.Exception);
            }
        }
    }
}