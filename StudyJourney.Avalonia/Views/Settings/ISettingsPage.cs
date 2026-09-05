using StudyJourney.Avalonia.Models;

namespace StudyJourney.Avalonia.Views.Settings;

/// <summary>
/// 设置页接口：Load 从设置读入控件，Apply 将控件写回设置。
/// IsDirty（#8）：本页是否有未保存修改 —— 设置窗口切页/关窗前据此提示，避免静默丢失。
/// 默认 false（无编辑状态的页面无需覆盖）；有直改列表/控件的页按需覆盖。
/// </summary>
public interface ISettingsPage
{
    void Load(AppSettings s);
    void Apply(AppSettings s);
    bool IsDirty => false;
}
