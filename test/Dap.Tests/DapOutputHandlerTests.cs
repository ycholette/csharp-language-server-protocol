using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using OmniSharp.Extensions.DebugAdapter.Protocol;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.JsonRpc.Client;
using OmniSharp.Extensions.JsonRpc.Server;
using OmniSharp.Extensions.JsonRpc.Server.Messages;
using Xunit;

namespace Dap.Tests
{
    public class DapOutputHandlerTests
    {
        private CancellationToken _cancellationToken;

        public DapOutputHandlerTests()
        {
            _cancellationToken = new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token;
        }

        private static OutputHandler NewHandler(PipeWriter writer)
        {
            var rec = Substitute.For<IReceiver>();
            rec.ShouldFilterOutput(Arg.Any<object>()).Returns(true);
            return new OutputHandler(writer, new DapSerializer(), rec, NullLogger<OutputHandler>.Instance);
        }

        [Fact]
        public async Task ShouldSerializeResponses()
        {
            var pipe = new Pipe(new PipeOptions());
            using var handler = NewHandler(pipe.Writer);

            var value = new OutgoingResponse(
                1, new object(),
                new Request(1, "command", new JObject())
            );

            handler.Send(value);

            var received = await GetReceived(pipe, handler, _cancellationToken);

            const string send =
                "Content-Length: 88\r\n\r\n{\"seq\":1,\"type\":\"response\",\"request_seq\":1,\"success\":true,\"command\":\"command\",\"body\":{}}";
            received.Should().Be(send);
        }

        [Fact]
        public async Task ShouldSerializeNotifications()
        {
            var pipe = new Pipe(new PipeOptions());
            using var handler = NewHandler(pipe.Writer);

            var value = new OutgoingNotification {
                Method = "method",
                Params = new object()
            };

            handler.Send(value);

            var received = await GetReceived(pipe, handler, _cancellationToken);

            const string send =
                "Content-Length: 51\r\n\r\n{\"seq\":1,\"type\":\"event\",\"event\":\"method\",\"body\":{}}";
            received.Should().Be(send);
        }

        [Fact]
        public async Task ShouldSerializeRequests()
        {
            var pipe = new Pipe(new PipeOptions());
            using var handler = NewHandler(pipe.Writer);

            var value = new OutgoingRequest {
                Method = "method",
                Id = 1,
                Params = new object(),
            };

            handler.Send(value);

            var received = await GetReceived(pipe, handler, _cancellationToken);

            const string send =
                "Content-Length: 60\r\n\r\n{\"seq\":1,\"type\":\"request\",\"command\":\"method\",\"arguments\":{}}";
            received.Should().Be(send);
        }

        [Fact]
        public async Task ShouldSerializeErrors()
        {
            var pipe = new Pipe(new PipeOptions());
            using var handler = NewHandler(pipe.Writer);

            var value = new RpcError(1, new ErrorMessage(1, "something", "data"));

            handler.Send(value);

            var received = await GetReceived(pipe, handler, _cancellationToken);

            const string send =
                "Content-Length: 148\r\n\r\n{\"seq\":1,\"type\":\"response\",\"request_seq\":1,\"success\":false,\"command\":\"\",\"message\":\"something\",\"body\":{\"code\":1,\"data\":\"data\",\"message\":\"something\"}}";
            received.Should().Be(send);
        }

        private static async Task<string> GetReceived(Pipe pipe, OutputHandler handler, CancellationToken cancellationToken)
        {
            var lastBufferLength = 0L;
            while (true)
            {
                var result = await pipe.Reader.ReadAsync(cancellationToken);
                if (lastBufferLength > 0 && lastBufferLength == result.Buffer.Length)
                {
                    var span = new byte[result.Buffer.Length].AsMemory();
                    result.Buffer.Slice(0, result.Buffer.Length).CopyTo(span.Span);
                    return System.Text.Encoding.UTF8.GetString(span.Span);
                }
                lastBufferLength = result.Buffer.Length;
                pipe.Reader.AdvanceTo(result.Buffer.Start);

                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
    }
}
