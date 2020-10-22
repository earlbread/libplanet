using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Libplanet.Net;
using Serilog;
using Xunit;
using Xunit.Abstractions;

namespace Libplanet.Tests.Net
{
    [Collection("Non Parallel Collection")]
    public class IceServerTest
    {
        private const int Timeout = 30 * 1000;

        public IceServerTest(ITestOutputHelper output)
        {
            const string outputTemplate =
                "{Timestamp:HH:mm:ss:ffffffZ}[@{SwarmId}][{ThreadId}] - {Message}";
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.WithThreadId()
                .WriteTo.TestOutput(output, outputTemplate: outputTemplate)
                .CreateLogger()
                .ForContext<SwarmTest>();
        }

        [FactOnlyTurnAvailable(Timeout = Timeout)]
        public async Task CreateTurnClient()
        {
            var turnUri = new Uri(
                Environment.GetEnvironmentVariable(
                    FactOnlyTurnAvailableAttribute.TurnUrlVarName));
            var userInfo = turnUri.UserInfo.Split(':');

            Log.Debug("Try stun://stun.l.google.com:19302");
            await Assert.ThrowsAsync<ArgumentException>(
                async () =>
                {
                    await IceServer.CreateTurnClient(
                       new[] { new IceServer(new[] { "stun://stun.l.google.com:19302" }) }
                    );
                }
            );
            var servers = new List<IceServer>()
            {
                new IceServer(new[] { "turn://turn.does-not-exists.org" }),
            };

            Log.Debug("Try turn://turn.does-not-exists.org");
            await Assert.ThrowsAsync<IceServerException>(
                async () => { await IceServer.CreateTurnClient(servers); });

            Log.Debug($"Try {turnUri}");
            servers.Add(new IceServer(new[] { turnUri }, userInfo[0], userInfo[1]));
            var turnClient = await IceServer.CreateTurnClient(servers);

            Assert.Equal(userInfo[0], turnClient.Username);
            Assert.Equal(userInfo[1], turnClient.Password);
        }
    }
}
