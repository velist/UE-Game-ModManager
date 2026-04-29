using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.ViewModels
{
    /// <summary>
    /// 方案管理 ViewModel。
    /// 对应原型 Screen 2（游戏方案管理）。
    /// </summary>
    public partial class ProfileViewModel : ObservableObject
    {
        private readonly ProfileService _profileService;
        private readonly ILogger _logger;

        /// <summary>所有方案列表。</summary>
        public ObservableCollection<InstanceProfile> Profiles { get; } = new();

        /// <summary>当前选中的方案。</summary>
        [ObservableProperty]
        private InstanceProfile? _selectedProfile;

        /// <summary>当前游戏名称。</summary>
        [ObservableProperty]
        private string _gameName = string.Empty;

        /// <summary>方案切换事件（通知主窗口刷新 MOD 列表）。</summary>
        public event Action? ProfileSwitched;

        public ProfileViewModel(ProfileService profileService, ILogger logger)
        {
            _profileService = profileService;
            _logger = logger;
        }

        /// <summary>
        /// 加载方案列表。
        /// </summary>
        public void LoadProfiles(string gameName)
        {
            GameName = gameName;
            Profiles.Clear();
            foreach (var p in _profileService.GetProfiles())
                Profiles.Add(p);

            SelectedProfile = Profiles.FirstOrDefault(p => p.IsActive)
                           ?? Profiles.FirstOrDefault();
        }

        /// <summary>
        /// 新建方案。
        /// </summary>
        [RelayCommand]
        public async Task CreateProfileAsync()
        {
            var name = $"方案 {Profiles.Count + 1}";
            var profile = await _profileService.CreateProfileAsync(name);
            Profiles.Add(profile);
            SelectedProfile = profile;
        }

        /// <summary>
        /// 复制当前方案。
        /// </summary>
        [RelayCommand]
        public async Task CloneProfileAsync()
        {
            if (SelectedProfile == null) return;
            var clone = await _profileService.CloneProfileAsync(
                SelectedProfile.Id, $"{SelectedProfile.Name} - 副本");
            Profiles.Add(clone);
            SelectedProfile = clone;
        }

        /// <summary>
        /// 删除当前方案。
        /// </summary>
        [RelayCommand]
        public async Task DeleteProfileAsync()
        {
            if (SelectedProfile == null || Profiles.Count <= 1) return;
            var toDelete = SelectedProfile;
            var success = await _profileService.DeleteProfileAsync(toDelete.Id);
            if (success)
            {
                Profiles.Remove(toDelete);
                SelectedProfile = Profiles.FirstOrDefault(p => p.IsActive)
                               ?? Profiles.FirstOrDefault();
            }
        }

        /// <summary>
        /// 切换到选中的方案。
        /// </summary>
        [RelayCommand]
        public async Task SwitchToProfileAsync(InstanceProfile? profile)
        {
            if (profile == null || profile.IsActive) return;

            await _profileService.SwitchProfileAsync(profile.Id);

            // 刷新列表状态
            foreach (var p in Profiles)
                p.IsActive = p.Id == profile.Id;

            SelectedProfile = profile;
            ProfileSwitched?.Invoke();
        }

        /// <summary>
        /// 重命名当前方案。
        /// </summary>
        public async Task RenameProfileAsync(string newName)
        {
            if (SelectedProfile == null || string.IsNullOrWhiteSpace(newName)) return;
            await _profileService.RenameProfileAsync(SelectedProfile.Id, newName);
            SelectedProfile.Name = newName;
            OnPropertyChanged(nameof(SelectedProfile));
        }
    }
}
