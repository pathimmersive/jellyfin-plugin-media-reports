using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Playback;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public class PlaybackCaptureTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Movie = Guid.NewGuid();

    private readonly Mock<ISessionManager> _sessions = new();
    private readonly List<SessionInfo> _live = [];
    private readonly ManualClock _clock = new();
    private TimeSpan _retention = TimeSpan.FromHours(24);
    private readonly PlaybackCapture _capture;

    public PlaybackCaptureTests()
    {
        _sessions.SetupGet(m => m.Sessions).Returns(() => _live);
        _capture = new PlaybackCapture(_sessions.Object, Mock.Of<ILibraryManager>(), _clock, () => _retention);
        _capture.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public void TwoDevicesOfOneUser_KeepSeparateEntries()
    {
        Progress(Session("tv", Alice, "Living Room TV", PlayMethod.Transcode, transcoding: true));
        Progress(Session("phone", Alice, "Phone", PlayMethod.DirectPlay));
        Stop(Session("tv", Alice, "Living Room TV"), 100);
        Stop(Session("phone", Alice, "Phone"), 200);

        var playbacks = _capture.GetPlaybacks(Alice);

        Assert.Equal(2, playbacks.Count);
        var tv = playbacks.Single(p => p.SessionId == "tv");
        var phone = playbacks.Single(p => p.SessionId == "phone");
        Assert.Equal("Living Room TV", tv.DeviceName);
        Assert.Equal("Transcode", tv.PlayMethod);
        Assert.NotNull(tv.Transcoding);
        Assert.Equal("Phone", phone.DeviceName);
        Assert.Equal("DirectPlay", phone.PlayMethod);
        Assert.Null(phone.Transcoding);
    }

    [Fact]
    public void TranscodeThenDirectPlay_DropsTranscodingDetails()
    {
        Progress(Session("tv", Alice, "TV", PlayMethod.Transcode, transcoding: true));
        Progress(Session("tv", Alice, "TV", PlayMethod.DirectPlay));
        Stop(Session("tv", Alice, "TV"), 100);

        var entry = Assert.Single(_capture.GetPlaybacks(Alice));

        Assert.Equal("DirectPlay", entry.PlayMethod);
        Assert.Null(entry.Transcoding);
    }

    [Fact]
    public void TranscodeWithoutDetailsOnALaterEvent_KeepsEarlierDetails()
    {
        Progress(Session("tv", Alice, "TV", PlayMethod.Transcode, transcoding: true));
        Progress(Session("tv", Alice, "TV", PlayMethod.Transcode));

        var entry = Assert.Single(_capture.GetPlaybacks(Alice));

        Assert.Equal(["AudioCodecNotSupported", "ContainerBitrateExceedsLimit"], entry.Transcoding!.TranscodeReasons.Order());
    }

    [Fact]
    public void ThinStopEvent_KeepsLastSeenStateAndTakesStopPosition()
    {
        Progress(Session("tv", Alice, "TV", PlayMethod.Transcode, transcoding: true, position: 50));
        Stop(Session("tv", Alice, "TV"), 180);

        var entry = Assert.Single(_capture.GetPlaybacks(Alice));

        Assert.Equal(PlaybackState.Stopped, entry.State);
        Assert.Equal(Movie, entry.ItemId);
        Assert.Equal("Transcode", entry.PlayMethod);
        Assert.Equal("h264", entry.Transcoding!.VideoCodec);
        Assert.Equal(180, entry.PositionTicks);
    }

    [Fact]
    public void SessionEndedWithoutStop_KeepsStateMarkedEnded()
    {
        Progress(Session("phone", Alice, "Phone", PlayMethod.Transcode, transcoding: true, position: 70));
        _sessions.Raise(m => m.SessionEnded += null, new SessionEventArgs { SessionInfo = Session("phone", Alice, "Phone") });

        var entry = Assert.Single(_capture.GetPlaybacks(Alice));

        Assert.Equal(PlaybackState.Ended, entry.State);
        Assert.Equal("Transcode", entry.PlayMethod);
        Assert.NotNull(entry.Transcoding);
        Assert.Equal(70, entry.PositionTicks);
    }

    [Fact]
    public void EntryOlderThanRetentionWindow_IsNotReturned()
    {
        Progress(Session("old", Alice, "TV", PlayMethod.DirectPlay));
        Stop(Session("old", Alice, "TV"), 1);
        _clock.Advance(TimeSpan.FromHours(23));
        Progress(Session("recent", Alice, "TV", PlayMethod.DirectPlay));
        Stop(Session("recent", Alice, "TV"), 1);
        _clock.Advance(TimeSpan.FromHours(2));

        var entry = Assert.Single(_capture.GetPlaybacks(Alice));

        Assert.Equal("recent", entry.SessionId);
    }

    [Fact]
    public void ShorterRetentionWindow_TakesEffectOnNextRead()
    {
        Progress(Session("tv", Alice, "TV", PlayMethod.DirectPlay));
        Stop(Session("tv", Alice, "TV"), 1);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(_capture.GetPlaybacks(Alice));

        _retention = TimeSpan.Zero;

        Assert.Empty(_capture.GetPlaybacks(Alice));
    }

    [Fact]
    public void ElevenSessions_ReturnsTenNewest()
    {
        for (var i = 0; i < 11; i++)
        {
            Progress(Session($"s{i}", Alice, "TV", PlayMethod.DirectPlay));
            Stop(Session($"s{i}", Alice, "TV"), 1);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        var playbacks = _capture.GetPlaybacks(Alice);

        Assert.Equal(10, playbacks.Count);
        Assert.Equal("s10", playbacks[0].SessionId);
        Assert.DoesNotContain(playbacks, p => p.SessionId == "s0");
    }

    [Fact]
    public void LivePlayback_IsReturnedAsPlayingWithItsTranscoding()
    {
        var session = Session("tv", Alice, "TV", PlayMethod.Transcode, transcoding: true, position: 40);
        _live.Add(session);

        var entry = Assert.Single(_capture.GetPlaybacks(Alice));

        Assert.Equal(PlaybackState.Playing, entry.State);
        Assert.Equal("Transcode", entry.PlayMethod);
        Assert.Equal("none", entry.Transcoding!.HardwareAccelerationType);
    }

    [Fact]
    public void NoSubtitleSelected_LeavesSubtitleIndexAbsent()
    {
        Progress(Session("tv", Alice, "TV", PlayMethod.DirectPlay, subtitleIndex: -1));

        Assert.Null(Assert.Single(_capture.GetPlaybacks(Alice)).SubtitleStreamIndex);
    }

    [Fact]
    public void OneUsersPlaybacks_NeverIncludeAnotherUsers()
    {
        Progress(Session("alice-tv", Alice, "TV", PlayMethod.DirectPlay));
        Progress(Session("bob-tv", Bob, "TV", PlayMethod.DirectPlay));
        _live.Add(Session("bob-phone", Bob, "Phone", PlayMethod.DirectPlay));

        Assert.Equal(["alice-tv"], _capture.GetPlaybacks(Alice).Select(p => p.SessionId));
    }

    [Fact]
    public async Task Endpoint_ReturnsOnlyTheCallersPlaybacks()
    {
        Progress(Session("alice-tv", Alice, "TV", PlayMethod.DirectPlay));
        Progress(Session("bob-tv", Bob, "TV", PlayMethod.DirectPlay));
        var authorization = new Mock<IAuthorizationContext>();
        authorization.Setup(a => a.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(new AuthorizationInfo { User = new Jellyfin.Database.Implementations.Entities.User("bob", "provider", "reset") { Id = Bob } });
        var controller = new PlaybacksController(_capture, authorization.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.GetPlaybacks();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var playbacks = Assert.IsAssignableFrom<IReadOnlyList<PlaybackEntry>>(ok.Value);
        Assert.Equal(["bob-tv"], playbacks.Select(p => p.SessionId));
    }

    [Fact]
    public void NewInstall_RetentionWindowIs24Hours()
    {
        Assert.Equal(24, new Configuration.PluginConfiguration().PlaybackRetentionHours);
    }

    private void Progress(SessionInfo session)
        => _sessions.Raise(m => m.PlaybackProgress += null, new PlaybackProgressEventArgs { Session = session });

    // The stop event delivers a session that no longer carries the item, play state or transcoding.
    private void Stop(SessionInfo session, long position)
        => _sessions.Raise(m => m.PlaybackStopped += null, new PlaybackStopEventArgs { Session = session, PlaybackPositionTicks = position });

    private static SessionInfo Session(
        string id,
        Guid user,
        string device,
        PlayMethod? playMethod = null,
        bool transcoding = false,
        long position = 0,
        int? subtitleIndex = null)
    {
        var session = new SessionInfo(Mock.Of<ISessionManager>(), NullLogger.Instance)
        {
            Id = id,
            UserId = user,
            Client = "Test Client",
            ApplicationVersion = "1.0.0",
            DeviceName = device,
            DeviceId = device + "-id",
        };
        if (playMethod is null)
        {
            return session;
        }

        session.NowPlayingItem = new BaseItemDto { Id = Movie, Name = "Test Movie" };
        session.PlayState = new PlayerStateInfo
        {
            PlayMethod = playMethod,
            MediaSourceId = Movie.ToString("N"),
            PositionTicks = position,
            AudioStreamIndex = 1,
            SubtitleStreamIndex = subtitleIndex,
        };
        if (transcoding)
        {
            session.TranscodingInfo = new TranscodingInfo
            {
                VideoCodec = "h264",
                AudioCodec = "aac",
                Container = "ts",
                Bitrate = 1_000_000,
                HardwareAccelerationType = MediaBrowser.Model.Entities.HardwareAccelerationType.none,
                TranscodeReasons = TranscodeReason.ContainerBitrateExceedsLimit | TranscodeReason.AudioCodecNotSupported,
            };
        }

        return session;
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
