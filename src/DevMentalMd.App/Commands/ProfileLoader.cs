using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;

namespace DevMentalMd.App.Commands;

/// <summary>
/// Discovers and loads key mapping profiles embedded as assembly resources.
/// Each profile is a JSON file under Commands/Profiles/ that maps command IDs
/// to gesture strings.
/// </summary>
public static class ProfileLoader {
    private static readonly JsonSerializerOptions JsonOpts = new() {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Profile identifiers in display order.</summary>
    public static readonly IReadOnlyList<string> ProfileIds = [
        "Default", "VSCode", "VisualStudio", "Emacs", "JetBrains", "Eclipse",
    ];

    /// <summary>
    /// Loads a profile by identifier. The identifier maps to an embedded
    /// resource named "DevMentalMd.App.Commands.Profiles.{id}.json".
    /// </summary>
    public static ProfileData Load(string profileId) {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = $"DevMentalMd.App.Commands.Profiles.{profileId}.json";

        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Missing embedded profile resource: {resourceName}");
        return JsonSerializer.Deserialize<ProfileData>(stream, JsonOpts)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize profile: {profileId}");
    }

    /// <summary>
    /// Returns the display name for a profile identifier.
    /// </summary>
    public static string GetDisplayName(string profileId) {
        var profile = Load(profileId);
        return string.IsNullOrEmpty(profile.Name) ? profileId : profile.Name;
    }
}
