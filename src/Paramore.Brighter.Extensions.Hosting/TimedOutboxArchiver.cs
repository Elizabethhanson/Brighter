#region Licence
/* The MIT License (MIT)
Copyright © 2024 Ian Cooper <ian_hammond_cooper@yahoo.co.uk>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the “Software”), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE. */

#endregion

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Paramore.Brighter.Logging;
// ReSharper disable StaticMemberInGenericType

namespace Paramore.Brighter.Extensions.Hosting
{
    /// <summary>
    /// The archiver will find messages in the outbox that are older than a certain age and archive them
    /// </summary>
    public class TimedOutboxArchiver<TMessage, TTransaction> : IHostedService, IDisposable where TMessage : Message
    {
        private static readonly ILogger s_logger = ApplicationLogging.CreateLogger<TimedOutboxSweeper>();
        private Timer _timer;
        private readonly OutboxArchiver<TMessage, TTransaction> _archiver;
        private readonly TimeSpan _dispatchedSince;
        private readonly IDistributedLock _distributedLock;
        private readonly TimedOutboxArchiverOptions _options;

        /// <summary>
        /// The archiver will find messages in the outbox that are older than a certain age and archive them
        /// </summary>
        /// <param name="archiver">The archiver to use</param>
        /// <param name="dispatchedSince">How old should a message be, in order to archive it?</param>
        /// <param name="distributedLock">Used to ensure that only one instance of the <see cref="TimedOutboxSweeper"/> is running</param>
        /// <param name="options">The <see cref="TimedOutboxArchiverOptions"/> that control how the archiver runs, such as interval</param>
        public TimedOutboxArchiver(
            OutboxArchiver<TMessage, TTransaction> archiver,
            TimeSpan dispatchedSince,
            IDistributedLock distributedLock,
            TimedOutboxArchiverOptions options)
        {
            _archiver = archiver;
            _dispatchedSince = dispatchedSince;
            _distributedLock = distributedLock;
            _options = options;
        }

        private const string LockingResourceName = "Archiver";

        /// <summary>
        /// Starts the archiver, which will run at an interval to find messages in the outbox that are older than a certain age and archive them
        /// </summary>
        /// <param name="cancellationToken">Will stop the archiver if signalled</param>
        /// <returns>A completed task to allow other background services to run</returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            s_logger.LogInformation("Outbox Archiver Service is starting");

            _timer = new Timer(async e => await Archive(e, cancellationToken), null, TimeSpan.Zero,
                TimeSpan.FromSeconds(_options.TimerInterval));

            return Task.CompletedTask;
        }

        /// <summary>
        /// Stops the archiver from running
        /// </summary>
        /// <param name="cancellationToken">Not used</param>
        /// <returns>A completed task to allow other background services to run</returns>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            s_logger.LogInformation("Outbox Archiver Service is stopping");

            _timer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Disposes of the timer resource used by the archiver
        /// </summary>
        public void Dispose()
        {
            _timer.Dispose();
        }

        private async Task Archive(object state, CancellationToken cancellationToken)
        {
            var lockId = await _distributedLock.ObtainLockAsync(LockingResourceName, cancellationToken); 
            if (lockId != null)
            {
                s_logger.LogInformation("Outbox Archiver looking for messages to Archive");
                try
                {
                    await _archiver.ArchiveAsync(_dispatchedSince, new RequestContext(), cancellationToken);
                }
                catch (Exception e)
                {
                    s_logger.LogError(e, "Error while sweeping the outbox");
                }
                finally
                {
                    await _distributedLock.ReleaseLockAsync(LockingResourceName, lockId, cancellationToken);
                }

                s_logger.LogInformation("Outbox Sweeper sleeping");
            }
            else
            {
                s_logger.LogWarning("Outbox Archiver is still running - abandoning attempt");
            }
            
        }
    }
}
