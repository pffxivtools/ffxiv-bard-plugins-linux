using System.Collections.Generic;
using System.Text;
using TinyIpc.Internal;
using TinyIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

public sealed class TinyMessageBusCrashHandlingTests
{
    [Fact]
    public async Task PublishAsync_BackendFailure_DegradesBusToDisabled_NoThrowOnLaterPublishes()
    {
        var inner = new ThrowingShimMessageBus(throwOnPublish: true);
        using var bus = new TinyMessageBus("faulty-publish", inner);

        await bus.PublishAsync(Encoding.UTF8.GetBytes("first"));
        await bus.PublishAsync(Encoding.UTF8.GetBytes("second"));

        Assert.Equal(1, inner.PublishCallCount);
    }

    [Fact]
    public void MessageReceived_HandlerFailure_IsContained()
    {
        var inner = new ThrowingShimMessageBus();
        using var bus = new TinyMessageBus("faulty-handler", inner);

        bus.MessageReceived += static (_, _) => throw new InvalidOperationException("boom");

        inner.RaiseMessage(Encoding.UTF8.GetBytes("payload"));

        Assert.Equal(1, bus.MessagesReceived);
    }

    [Fact]
    public void Dispose_BackendFailure_DoesNotThrow()
    {
        var inner = new ThrowingShimMessageBus(throwOnDispose: true);
        var bus = new TinyMessageBus("faulty-dispose", inner);

        bus.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_BackendFailure_DoesNotThrow()
    {
        var inner = new ThrowingShimMessageBus(throwOnDisposeAsync: true);
        var bus = new TinyMessageBus("faulty-dispose-async", inner);

        await bus.DisposeAsync();
    }

    private sealed class ThrowingShimMessageBus : IShimTinyMessageBus
    {
        private readonly bool _throwOnPublish;
        private readonly bool _throwOnDispose;
        private readonly bool _throwOnDisposeAsync;

        public ThrowingShimMessageBus(
            bool throwOnPublish = false,
            bool throwOnDispose = false,
            bool throwOnDisposeAsync = false)
        {
            _throwOnPublish = throwOnPublish;
            _throwOnDispose = throwOnDispose;
            _throwOnDisposeAsync = throwOnDisposeAsync;
        }

        public event EventHandler<TinyMessageReceivedEventArgs>? MessageReceived;

        public int PublishCallCount { get; private set; }

        public Task PublishAsync(byte[] message)
        {
            PublishCallCount++;
            if (_throwOnPublish)
                throw new InvalidOperationException("publish failure");

            return Task.CompletedTask;
        }

        public void RaiseMessage(byte[] payload)
            => MessageReceived?.Invoke(this, new TinyMessageReceivedEventArgs((IReadOnlyList<byte>)payload));

        public void Dispose()
        {
            if (_throwOnDispose)
                throw new InvalidOperationException("dispose failure");
        }

        public ValueTask DisposeAsync()
        {
            if (_throwOnDisposeAsync)
                throw new InvalidOperationException("dispose async failure");

            return ValueTask.CompletedTask;
        }
    }
}
