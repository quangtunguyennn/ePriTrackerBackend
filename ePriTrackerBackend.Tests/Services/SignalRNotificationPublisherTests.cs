using ePriTrackerBackend.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace ePriTrackerBackend.Tests.Services
{
    public class SignalRNotificationPublisherTests
    {
        private sealed class RecordingProxy : IClientProxy
        {
            public List<string> Methods { get; } = new();
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                Methods.Add(method);
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingClients : IHubClients
        {
            public RecordingProxy Proxy { get; } = new();
            public List<string> Users { get; private set; } = new();

            IClientProxy IHubClients<IClientProxy>.Users(IReadOnlyList<string> userIds) { Users = userIds.ToList(); return Proxy; }

            public IClientProxy All => throw new NotSupportedException();
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IClientProxy Client(string connectionId) => throw new NotSupportedException();
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
            public IClientProxy Group(string groupName) => throw new NotSupportedException();
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
            public IClientProxy User(string userId) => throw new NotSupportedException();
        }

        private sealed class FakeHubContext : IHubContext<NotificationHub>
        {
            public RecordingClients RecordingClients { get; } = new();
            public IHubClients Clients => RecordingClients;
            public IGroupManager Groups => throw new NotSupportedException();
        }

        [Fact]
        public async Task SendsSignalToEachUserOnce_ByUserId()
        {
            var hub = new FakeHubContext();
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();

            await new SignalRNotificationPublisher(hub).NotifyChangedAsync(new[] { a, b, a });

            Assert.Equal(new[] { a.ToString(), b.ToString() }, hub.RecordingClients.Users);
            Assert.Equal(new[] { NotificationHub.NotificationsChangedEvent }, hub.RecordingClients.Proxy.Methods);
        }

        [Fact]
        public async Task NoUsers_SendsNothing()
        {
            var hub = new FakeHubContext();
            await new SignalRNotificationPublisher(hub).NotifyChangedAsync(Array.Empty<Guid>());
            Assert.Empty(hub.RecordingClients.Proxy.Methods);
        }

        [Fact]
        public void ResolvesWithRealSignalR()
        {
            using var provider = new ServiceCollection().AddLogging().AddSignalR().Services
                .AddSingleton<SignalRNotificationPublisher>()
                .BuildServiceProvider();
            Assert.NotNull(provider.GetRequiredService<SignalRNotificationPublisher>());
        }
    }
}
