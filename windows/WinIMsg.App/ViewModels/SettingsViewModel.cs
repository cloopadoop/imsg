using System.Collections.ObjectModel;
using WinIMsg.App.Mvvm;
using WinIMsg.Core.Models;

namespace WinIMsg.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private WinIMsgSettings _settings = new();
    private ImsgBridgeProfile _editingProfile = ImsgBridgeProfile.Default();
    private bool _isGeneralDirty;
    private bool _isProfileDirty;
    private string _contactNamesText = string.Empty;
    private string _advancedFeatureText = string.Empty;

    public ObservableCollection<ImsgBridgeProfile> Profiles { get; } = [];

    public WinIMsgSettings Settings
    {
        get => _settings;
        private set => SetProperty(ref _settings, value);
    }

    public ImsgBridgeProfile EditingProfile
    {
        get => _editingProfile;
        private set => SetProperty(ref _editingProfile, value);
    }

    public bool IsGeneralDirty
    {
        get => _isGeneralDirty;
        private set => SetProperty(ref _isGeneralDirty, value);
    }

    public bool IsProfileDirty
    {
        get => _isProfileDirty;
        private set => SetProperty(ref _isProfileDirty, value);
    }

    public string ContactNamesText
    {
        get => _contactNamesText;
        set => SetProperty(ref _contactNamesText, value);
    }

    public string AdvancedFeatureText
    {
        get => _advancedFeatureText;
        set => SetProperty(ref _advancedFeatureText, value);
    }

    public void Load(WinIMsgSettings settings)
    {
        Settings = settings.Normalize();
        Profiles.ReplaceWith(Settings.Profiles);
        EditingProfile = Settings.ActiveProfile;
        IsGeneralDirty = false;
        IsProfileDirty = false;
    }

    public void MarkGeneralDirty() => IsGeneralDirty = true;

    public void MarkProfileDirty() => IsProfileDirty = true;

    public void SetGeneralDirty(bool isDirty) => IsGeneralDirty = isDirty;

    public void SetProfileDirty(bool isDirty) => IsProfileDirty = isDirty;

    public void SetEditingProfile(string profileId)
    {
        EditingProfile = Settings.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
            ?? Settings.ActiveProfile;
        IsProfileDirty = false;
    }

    public void ReplaceSettings(WinIMsgSettings settings)
    {
        Load(settings);
    }

    public WinIMsgSettings SaveGeneral(
        bool autoDownloadAttachments,
        bool startWithWindows,
        bool minimizeToTray,
        bool enableNotifications,
        bool showMessageContentInNotifications,
        bool syncCacheInBackground,
        bool mergeChatsByParticipants,
        string? phoneNumberRegion,
        bool sendTypingIndicators = false,
        bool? enableWebCompanion = null)
    {
        Settings = Settings with
        {
            AutoDownloadAttachments = autoDownloadAttachments,
            StartWithWindows = startWithWindows,
            MinimizeToTray = minimizeToTray,
            EnableNotifications = enableNotifications,
            ShowMessageContentInNotifications = showMessageContentInNotifications,
            SendTypingIndicators = sendTypingIndicators,
            EnableWebCompanion = enableWebCompanion ?? Settings.EnableWebCompanion,
            SyncCacheInBackground = syncCacheInBackground,
            MergeChatsByParticipants = mergeChatsByParticipants,
            PhoneNumberRegion = phoneNumberRegion ?? "AUTO"
        };
        Settings = Settings.Normalize();
        Profiles.ReplaceWith(Settings.Profiles);
        EditingProfile = Settings.ActiveProfile;
        IsGeneralDirty = false;
        return Settings;
    }

    public WinIMsgSettings SaveProfile(ImsgBridgeProfile profile, bool makeActive = false)
    {
        var normalizedProfile = profile.Normalize();
        var profiles = Settings.Profiles
            .Select(existing => string.Equals(existing.Id, normalizedProfile.Id, StringComparison.OrdinalIgnoreCase) ? normalizedProfile : existing)
            .ToList();

        if (!profiles.Any(existing => string.Equals(existing.Id, normalizedProfile.Id, StringComparison.OrdinalIgnoreCase)))
        {
            profiles.Add(normalizedProfile);
        }

        Settings = Settings with
        {
            ActiveProfileId = makeActive ? normalizedProfile.Id : Settings.ActiveProfileId,
            Profiles = profiles
        };
        Settings = Settings.Normalize();
        Profiles.ReplaceWith(Settings.Profiles);
        EditingProfile = Settings.Profiles.FirstOrDefault(existing =>
            string.Equals(existing.Id, normalizedProfile.Id, StringComparison.OrdinalIgnoreCase))
            ?? Settings.ActiveProfile;
        IsProfileDirty = false;
        return Settings;
    }

    public ImsgBridgeProfile AddProfile(int sequence)
    {
        var profile = ImsgBridgeProfile.Default() with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"Mac {sequence}"
        };

        Settings = Settings with
        {
            ActiveProfileId = profile.Id,
            Profiles = [.. Settings.Profiles, profile]
        };
        Settings = Settings.Normalize();
        Profiles.ReplaceWith(Settings.Profiles);
        EditingProfile = Settings.ActiveProfile;
        IsProfileDirty = false;
        IsGeneralDirty = false;
        return EditingProfile;
    }

    public WinIMsgSettings DeleteProfile(string profileId)
    {
        if (Settings.Profiles.Count <= 1)
        {
            return Settings;
        }

        var remainingProfiles = Settings.Profiles
            .Where(profile => !string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (remainingProfiles.Count == Settings.Profiles.Count)
        {
            return Settings;
        }

        var activeProfileId = string.Equals(Settings.ActiveProfileId, profileId, StringComparison.OrdinalIgnoreCase)
            ? remainingProfiles[0].Id
            : Settings.ActiveProfileId;
        Settings = Settings with
        {
            ActiveProfileId = activeProfileId,
            Profiles = remainingProfiles
        };
        Settings = Settings.Normalize();
        Profiles.ReplaceWith(Settings.Profiles);
        EditingProfile = Settings.ActiveProfile;
        IsProfileDirty = false;
        return Settings;
    }

    public WinIMsgSettings SelectActiveProfile(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) ||
            !Settings.Profiles.Any(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)))
        {
            return Settings;
        }

        Settings = (Settings with { ActiveProfileId = profileId }).Normalize();
        Profiles.ReplaceWith(Settings.Profiles);
        EditingProfile = Settings.ActiveProfile;
        IsProfileDirty = false;
        return Settings;
    }
}
