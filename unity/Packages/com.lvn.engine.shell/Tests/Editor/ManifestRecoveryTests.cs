using System;
using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    public sealed class ManifestRecoveryTests
    {
        [TestCase(0, "network")]
        [TestCase(429, "http_429")]
        [TestCase(503, "http_503")]
        [TestCase(404, "http_404")]
        public void FetchFailureSelectsNetworkMessage(int status, string code)
        {
            Assert.AreEqual(ManifestFailureKind.Network,
                ManifestRecovery.KindFor(new LvnFetchException(status, code, "fetch failed")));
        }

        private static IEnumerable<Exception> AppFailures()
        {
            yield return new TypeInitializationException("ExampleInitializer", new InvalidOperationException("initialization failed"));
            yield return new InvalidOperationException("network (0): misleading message");
            yield return new NullReferenceException();
            yield return new FormatException();
            yield return new OperationCanceledException();
            yield return new Exception("fetch failed");
            // A wrapper is an application failure too; an inner fetch error
            // does not turn a failed initializer into a recoverable connection.
            yield return new TypeInitializationException("ExampleInitializer", new LvnFetchException(0, "network", "offline"));
        }

        [TestCaseSource(nameof(AppFailures))]
        public void ApplicationFailureSelectsAppErrorMessage(Exception error)
        {
            Assert.AreEqual(ManifestFailureKind.AppError, ManifestRecovery.KindFor(error));
        }

        [Test]
        public void PersistentInitializerFailureUsesIncreasingCappedBackoff()
        {
            var error = new TypeInitializationException("ExampleInitializer", new InvalidOperationException());
            var kind = ManifestRecovery.KindFor(error);
            float previous = 0;
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                float pause = ManifestRecovery.PauseSeconds(kind, attempt);
                Assert.AreEqual(LvnBackoff.DelaySeconds(attempt + 1), pause);
                if (previous < LvnBackoff.DefaultCapSeconds) Assert.Greater(pause, previous);
                Assert.LessOrEqual(pause, LvnBackoff.DefaultCapSeconds);
                previous = pause;
            }
            Assert.AreEqual(LvnBackoff.DefaultCapSeconds, previous);
        }

        [TestCase(1)]
        [TestCase(5)]
        [TestCase(100)]
        public void NetworkRecoveryKeepsItsExistingPause(int attempt)
        {
            var kind = ManifestRecovery.KindFor(new LvnFetchException(0, "network", "offline"));
            Assert.AreEqual(5f, ManifestRecovery.PauseSeconds(kind, attempt));
        }
    }
}
