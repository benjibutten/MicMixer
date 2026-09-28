using AwesomeAssertions;
using MicMixer.Input;
using Xunit;

namespace MicMixer.Tests;

public sealed class SendingKeyHolderTests
{
    private static readonly FunctionKey F24 = FunctionKey.Parse("F24");
    private static readonly FunctionKey F15 = FunctionKey.Parse("F15");

    private readonly List<(string Key, bool Down)> _sent = [];
    private readonly HashSet<string> _keysDownInWindows = [];
    private readonly SendingKeyHolder _holder;
    /// <summary>When set, Windows loses injected events, as it does under UIPI.</summary>
    private bool _dropEvents;

    public SendingKeyHolderTests()
    {
        _holder = new SendingKeyHolder(Inject, key => _keysDownInWindows.Contains(key.Name)) { Key = F24 };
    }

    private void Inject(FunctionKey key, bool down)
    {
        _sent.Add((key.Name, down));
        if (_dropEvents)
        {
            return;
        }

        if (down)
        {
            _keysDownInWindows.Add(key.Name);
        }
        else
        {
            _keysDownInWindows.Remove(key.Name);
        }
    }

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void Update_ShouldPressTheKeyOnce_WhileSending()
    {
        _holder.Update(sending: true, Ms(0));
        _holder.Update(sending: true, Ms(50));
        _holder.Update(sending: true, Ms(900));

        _sent.Should().Equal(("F24", true));
    }

    [Fact]
    public void Update_ShouldReleaseTheKey_OnlyAfterTheReleaseDelay()
    {
        _holder.Update(sending: true, Ms(0));
        _holder.Update(sending: false, Ms(500));
        _holder.Update(sending: false, Ms(599));

        _sent.Should().Equal(("F24", true));

        _holder.Update(sending: false, Ms(600));

        _sent.Should().Equal(("F24", true), ("F24", false));
    }

    [Fact]
    public void Update_ShouldKeepHolding_WhenSendingResumesWithinTheReleaseDelay()
    {
        _holder.Update(sending: true, Ms(0));
        _holder.Update(sending: false, Ms(500));
        _holder.Update(sending: true, Ms(550));
        _holder.Update(sending: false, Ms(800));
        _holder.Update(sending: false, Ms(850));

        _sent.Should().Equal(("F24", true));
    }

    [Fact]
    public void Update_ShouldInjectNothing_WhileWindowsHasTheWantedState()
    {
        for (int ms = 0; ms <= 10_000; ms += 50)
        {
            _holder.Update(sending: ms is >= 2_000 and < 6_000, Ms(ms));
        }

        _sent.Should().Equal(("F24", true), ("F24", false));
    }

    [Fact]
    public void Update_ShouldResendOncePerSecond_UntilWindowsTakesTheEvent()
    {
        _dropEvents = true;
        _holder.Update(sending: true, Ms(0));
        _holder.Update(sending: true, Ms(999));
        _holder.Update(sending: true, Ms(1_000));

        _dropEvents = false;
        _holder.Update(sending: true, Ms(2_000));
        _holder.Update(sending: true, Ms(5_000));

        _sent.Should().Equal(("F24", true), ("F24", true), ("F24", true));
    }

    [Fact]
    public void Update_ShouldResendAKeyUpThatWindowsDropped()
    {
        _holder.Update(sending: true, Ms(0));
        _dropEvents = true;
        _holder.Update(sending: false, Ms(1_000));
        _holder.Update(sending: false, Ms(1_100));

        _dropEvents = false;
        _holder.Update(sending: false, Ms(2_100));

        _sent.Should().Equal(("F24", true), ("F24", false), ("F24", false));
        _keysDownInWindows.Should().BeEmpty();
    }

    [Fact]
    public void Update_ShouldResendADroppedKeyUp_AfterSendingStoppedForGood()
    {
        _holder.Update(sending: true, Ms(0));
        _dropEvents = true;
        _holder.Release();
        _holder.Update(sending: false, Ms(1_000));

        _dropEvents = false;
        _holder.Update(sending: false, Ms(2_000));
        _holder.Update(sending: false, Ms(3_000));

        _sent.Should().Equal(("F24", true), ("F24", false), ("F24", false), ("F24", false));
        _keysDownInWindows.Should().BeEmpty();
    }

    [Fact]
    public void Update_ShouldResendADroppedKeyUp_OfAKeyNoLongerInUse()
    {
        _holder.Update(sending: true, Ms(0));
        _dropEvents = true;
        _holder.Key = null;
        _holder.Update(sending: false, Ms(1_000));

        _dropEvents = false;
        _holder.Update(sending: false, Ms(2_000));

        _keysDownInWindows.Should().BeEmpty();
    }

    [Fact]
    public void Update_ShouldLeaveAKeyTheUserHolds_WhileNotSending()
    {
        _keysDownInWindows.Add("F24");

        for (int ms = 0; ms <= 5_000; ms += 50)
        {
            _holder.Update(sending: false, Ms(ms));
        }

        _sent.Should().BeEmpty();
    }

    [Fact]
    public void Update_ShouldPressAgain_WhenSomethingElseReleasedTheKey()
    {
        _holder.Update(sending: true, Ms(0));
        _keysDownInWindows.Clear();

        _holder.Update(sending: true, Ms(500));
        _holder.Update(sending: true, Ms(1_000));

        _sent.Should().Equal(("F24", true), ("F24", true));
    }

    [Fact]
    public void ChangingTheKey_ShouldReleaseTheOldKey_AndPressTheNewOneOnTheNextUpdate()
    {
        _holder.Update(sending: true, Ms(0));

        _holder.Key = F15;
        _holder.Update(sending: true, Ms(50));

        _sent.Should().Equal(("F24", true), ("F24", false), ("F15", true));
    }

    [Fact]
    public void TurningTheFeatureOff_ShouldReleaseTheKey_AndSendNothingMore()
    {
        _holder.Update(sending: true, Ms(0));

        _holder.Key = null;
        _holder.Update(sending: true, Ms(2_000));

        _sent.Should().Equal(("F24", true), ("F24", false));
    }

    [Fact]
    public void Release_ShouldSendKeyUpAtOnce_AndPressAgainWhenStillSending()
    {
        _holder.Update(sending: true, Ms(0));

        _holder.Release();
        _holder.Update(sending: true, Ms(50));

        _sent.Should().Equal(("F24", true), ("F24", false), ("F24", true));
    }

    [Theory]
    [InlineData("F13", 0x7C)]
    [InlineData("f24", 0x87)]
    [InlineData("F12", 0x87)]
    [InlineData(null, 0x87)]
    public void Parse_ShouldIgnoreCase_AndFallBackToF24(string? name, int virtualKey)
    {
        FunctionKey.Parse(name).VirtualKey.Should().Be((ushort)virtualKey);
    }
}
