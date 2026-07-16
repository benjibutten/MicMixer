using AwesomeAssertions;
using MicMixer.Settings;
using Xunit;

namespace MicMixer.Tests;

public sealed class StartupRegistrySyncServiceTests
{
    [Fact]
    public void Sync_ShouldWriteMinimizedRunCommand_WhenEnabled()
    {
        var store = new RecordingStartupRegistryStore();
        var sut = new StartupRegistrySyncService(new RecordingStartupRegistryStoreFactory(store));
        const string exePath = @"C:\Apps\MicMixer\MicMixer.exe";

        bool synced = sut.Sync(startWithWindows: true, exePath);

        synced.Should().BeTrue();
        store.SetName.Should().Be(StartupRegistrySyncService.AppRegistryName);
        store.SetValueText.Should().Be($"\"{exePath}\" --minimized");
        store.DeleteCalled.Should().BeFalse();
    }

    [Fact]
    public void Sync_ShouldDeleteRunEntry_WhenDisabled()
    {
        var store = new RecordingStartupRegistryStore();
        var sut = new StartupRegistrySyncService(new RecordingStartupRegistryStoreFactory(store));

        bool synced = sut.Sync(startWithWindows: false, @"C:\Apps\MicMixer\MicMixer.exe");

        synced.Should().BeTrue();
        store.DeleteCalled.Should().BeTrue();
        store.DeletedName.Should().Be(StartupRegistrySyncService.AppRegistryName);
    }

    [Theory]
    [InlineData("--minimized")]
    [InlineData("--MINIMIZED")]
    public void StartupArgument_ShouldBeCaseInsensitive(string argument)
    {
        App.HasStartHiddenInTrayArgument([argument]).Should().BeTrue();
    }

    private sealed class RecordingStartupRegistryStoreFactory(IStartupRegistryStore store)
        : IStartupRegistryStoreFactory
    {
        public IStartupRegistryStore? OpenCurrentUserRunKey() => store;
    }

    private sealed class RecordingStartupRegistryStore : IStartupRegistryStore
    {
        public string? SetName { get; private set; }

        public string? SetValueText { get; private set; }

        public string? DeletedName { get; private set; }

        public bool DeleteCalled { get; private set; }

        public void SetValue(string name, string value)
        {
            SetName = name;
            SetValueText = value;
        }

        public void DeleteValue(string name, bool throwOnMissingValue)
        {
            DeletedName = name;
            DeleteCalled = true;
        }

        public void Dispose()
        {
        }
    }
}
