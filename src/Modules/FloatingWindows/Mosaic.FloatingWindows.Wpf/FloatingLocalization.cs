using System.Windows;
using Mosaic.Presentation.Wpf;
namespace Mosaic.FloatingWindows.Wpf;
internal static class FloatingLocalization
{
    internal static string Text(string key,string fallback)=>MosaicLocalizationManager.TryFindResource(key)??fallback;
    internal static ResourceDictionary Dictionary(MosaicLanguage language)
    {
        var english=language==MosaicLanguage.English;
        var result=new ResourceDictionary();
        foreach(var (key,zh,en) in new[]{("Title","悬浮任务","Floating task"),("Pin","置顶 / 取消置顶","Toggle always on top"),("Close","关闭小窗","Close this window"),("Restore","打开主窗口","Open main window"),("Move","拖动以移动窗口","Drag to move window"),("SaveError","布局尚未保存，请重试","Layout was not saved. Retry"),("Retry","重试保存","Retry save")})result["Floating."+key]=english?en:zh;
        result["Settings.Section.FloatingWindows"]=english?"Floating tasks":"悬浮任务窗";
        var definitions=new FloatingSettings(_=>Task.CompletedTask).Definitions;
        var names=new[]{"Restore windows on startup","New windows always on top","Compact width","Expanded width","Expanded height","Minimum window width","Minimum expanded height","Maximum open windows","Layout save delay"};
        var descriptions=new[]{"Restore windows that were open when the app exited","Existing windows retain their own topmost choice","Initial compact width in DIP","Initial expanded width in DIP","Initial expanded height in DIP","Reversible preview; saved sizes remain unchanged","Reversible preview; saved sizes remain unchanged","Excess recovery records remain saved","Milliseconds; pending layouts are flushed on exit"};
        for(var i=0;i<definitions.Count;i++){result[definitions[i].DisplayNameKey!]=english?names[i]:definitions[i].DisplayName;result[definitions[i].DescriptionKey!]=english?descriptions[i]:definitions[i].Description;}
        return result;
    }
}
