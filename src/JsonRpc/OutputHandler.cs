using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipelines;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OmniSharp.Extensions.JsonRpc
{
    public class OutputHandler : IOutputHandler
    {
        private readonly PipeWriter _pipeWriter;
        private readonly ISerializer _serializer;
        private readonly IReceiver _receiver;
        private readonly ILogger<OutputHandler> _logger;
        private readonly BlockingCollection<object> _queue;
        private readonly TaskCompletionSource<object> _outputIsFinished;
        private readonly CompositeDisposable _disposable;
        private readonly CancellationTokenSource _done;
        private bool _triggerShutDown = false;

        public OutputHandler(
            PipeWriter pipeWriter,
            ISerializer serializer,
            IReceiver receiver,
            ILogger<OutputHandler> logger
        )
        {
            _pipeWriter = pipeWriter;
            _serializer = serializer;
            _receiver = receiver;
            _logger = logger;
            _queue = new BlockingCollection<object>();
            _outputIsFinished = new TaskCompletionSource<object>();
            _done = new CancellationTokenSource();
            Task.Run(() => HandleOutputStream(_done.Token), _done.Token);

            _disposable = new CompositeDisposable {
                _queue,
                _done
            };
        }

        public void Send(object value)
        {
            if (_queue.IsCompleted) return;
            if (_queue.IsAddingCompleted) return;
            _queue.Add(value);
        }

        public async Task StopAsync()
        {
            await _pipeWriter.CompleteAsync();
        }

        /// <summary>
        /// For unit test use only
        /// </summary>
        /// <returns></returns>
        [EditorBrowsable(EditorBrowsableState.Never)]
        internal void TriggerShutdown()
        {
            _triggerShutDown = true;
            _queue.CompleteAdding();
        }

        private async Task HandleOutputStream(CancellationToken cancellationToken)
        {
            while (!(cancellationToken.IsCancellationRequested || _queue.IsCompleted) && !_triggerShutDown)
            {
                while (_queue.TryTake(out var value, 100, cancellationToken))
                {
                    if (!_receiver.ShouldFilterOutput(value)) continue;
                    await ProcessOutputStream(value, cancellationToken);
                }
                _logger.LogTrace("OutputHandler timeout, restarting");
            }

            _outputIsFinished.TrySetResult(null);
        }

        private async Task ProcessOutputStream(object value, CancellationToken cancellationToken)
        {
            try
            {
                // TODO: this will be part of the serialization refactor to make streaming first class
                var content = _serializer.SerializeObject(value);
                var contentBytes = Encoding.UTF8.GetBytes(content).AsMemory();
                await _pipeWriter.WriteAsync(Encoding.UTF8.GetBytes($"Content-Length: {contentBytes.Length}\r\n\r\n"), cancellationToken);
                await _pipeWriter.WriteAsync(contentBytes, cancellationToken);
                await _pipeWriter.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken != cancellationToken)
            {
                _logger.LogTrace(ex, "Cancellation happened");
                Error(ex);
            }
            catch (Exception e)
            {
                _logger.LogTrace(e, "Could not write to output handler, perhaps serialization failed?");
                Error(e);
            }
        }

        public Task WaitForShutdown() => _outputIsFinished.Task;

        private void Error(Exception ex)
        {
            _done.Cancel();
            _outputIsFinished.TrySetResult(ex);
        }

        public void Dispose()
        {
            _outputIsFinished.TrySetResult(null);
            _disposable.Dispose();
        }
    }
}
