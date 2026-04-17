using System.Runtime.InteropServices;
using DMEdit.App.Services;

namespace DMEdit.App.Tests;

/// <summary>
/// Tests for <see cref="LinuxFilePickerResolver"/>.  The resolver's inputs
/// include a cached probe result and zenity availability that we don't
/// mock here — these tests verify the portable invariants (non-Linux always
/// Native, mode=Native always Native, the descriptor helper is non-empty).
/// Probe-dependent branches are exercised on Linux CI.
/// </summary>
public class LinuxFilePickerResolverTests {
    [Fact]
    public void Resolve_ReturnsXdgPortal_OnNonLinux_RegardlessOfMode() {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return; // skip on Linux
        Assert.Equal(FilePickerChoice.XdgPortal,
            LinuxFilePickerResolver.Resolve(LinuxFilePickerMode.Auto));
        Assert.Equal(FilePickerChoice.XdgPortal,
            LinuxFilePickerResolver.Resolve(LinuxFilePickerMode.XdgPortal));
        Assert.Equal(FilePickerChoice.XdgPortal,
            LinuxFilePickerResolver.Resolve(LinuxFilePickerMode.Zenity));
    }

    [Fact]
    public void Resolve_ForceXdgPortal_AlwaysReturnsXdgPortal() {
        Assert.Equal(FilePickerChoice.XdgPortal,
            LinuxFilePickerResolver.Resolve(LinuxFilePickerMode.XdgPortal));
    }

    [Fact]
    public void DescribeCurrent_ReturnsNonEmptyString_ForAllModes() {
        foreach (var mode in new[] {
            LinuxFilePickerMode.Auto,
            LinuxFilePickerMode.XdgPortal,
            LinuxFilePickerMode.Zenity,
            LinuxFilePickerMode.KDialog,
        }) {
            var desc = LinuxFilePickerResolver.DescribeCurrent(mode);
            Assert.False(string.IsNullOrWhiteSpace(desc), $"DescribeCurrent({mode}) was empty");
        }
    }

    [Fact]
    public void IsZenityAvailable_DoesNotThrow() {
        // Just verify the property is accessible and returns a deterministic bool.
        var first = LinuxFilePickerResolver.IsZenityAvailable;
        var second = LinuxFilePickerResolver.IsZenityAvailable;
        Assert.Equal(first, second);
    }

    [Fact]
    public void IsKDialogAvailable_DoesNotThrow() {
        var first = LinuxFilePickerResolver.IsKDialogAvailable;
        var second = LinuxFilePickerResolver.IsKDialogAvailable;
        Assert.Equal(first, second);
    }

    [Fact]
    public void Resolve_KDialogMode_FallsBackToXdgPortal_WhenNotInstalled() {
        // When kdialog isn't installed the resolver must not return KDialog —
        // otherwise callers would try to launch a missing binary.
        if (LinuxFilePickerResolver.IsKDialogAvailable) return; // skip if kdialog is actually installed
        Assert.Equal(FilePickerChoice.XdgPortal,
            LinuxFilePickerResolver.Resolve(LinuxFilePickerMode.KDialog));
    }
}

public class LinuxPortalProbeTests {
    [Fact]
    public void Result_IsCached() {
        var first = LinuxPortalProbe.Result;
        var second = LinuxPortalProbe.Result;
        Assert.Equal(first.IsHealthy, second.IsHealthy);
        Assert.Equal(first.Diagnostic, second.Diagnostic);
    }

    [Fact]
    public void Result_OnNonLinux_IsHealthy() {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return; // skip on Linux
        Assert.True(LinuxPortalProbe.Result.IsHealthy);
    }

    [Fact]
    public void Result_HasNonEmptyDiagnostic() {
        Assert.False(string.IsNullOrWhiteSpace(LinuxPortalProbe.Result.Diagnostic));
    }
}
