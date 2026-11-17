using Mosaic.Contracts;
using Mosaic.Settings.Contracts;
namespace Mosaic.FloatingWindows.Wpf;
internal sealed class FloatingSettings(Func<IReadOnlyDictionary<string,object?>,Task> apply):ISettingSource
{
    public string ModuleId=>"floating-windows";
    public IReadOnlyList<SettingDefinition> Definitions {get;} =
    [
        Define("floating.restoreOnStartup","启动时恢复小窗","下次启动恢复尚未单独关闭的任务小窗",true,normal:true,restart:true),
        Define("floating.defaultTopmost","新小窗默认置顶","已有窗口保留各自的置顶选择",true,normal:true),
        Define("floating.defaultCompactWidth","紧凑宽度","新开小窗的紧凑宽度（DIP）",320,220,640),
        Define("floating.defaultExpandedWidth","展开宽度","新开小窗的展开宽度（DIP）",320,220,960),
        Define("floating.defaultExpandedHeight","展开高度","新开小窗的展开高度（DIP）",360,180,1000),
        Define("floating.minWidth","小窗最小宽度","可逆预览，不覆盖已保存的窗口尺寸",220,180,480,preview:true),
        Define("floating.minExpandedHeight","展开最小高度","可逆预览，不覆盖已保存的窗口尺寸",180,120,480,preview:true),
        Define("floating.maxOpenWindows","小窗数量上限","降低后不关闭已有窗口，超额恢复记录保留",8,1,32),
        Define("floating.boundsSaveDelayMilliseconds","布局保存延迟","移动缩放后的合并保存延迟（毫秒）；退出强制排空",300,50,2000),
        Define("floating.dragBandHeight","顶部拖动带","顶部可见区域的拖动命中高度（DIP），不增加留白",12,8,20,preview:true),
        Define("floating.cardPadding","卡片内边距","外框内边距（DIP）；预览不覆盖保存尺寸",8,4,16,preview:true),
        Define("floating.contentGap","内容间距","内容内部行间距（DIP）",4,0,12,preview:true)
    ];
    private static SettingDefinition Define(string id,string title,string description,object value,int? min=null,int? max=null,bool normal=false,bool restart=false,bool preview=false)=>
        new(id,"floating-windows",title,description,value is bool?SettingValueKind.Boolean:SettingValueKind.Integer,value,min,max,null,SettingsGroup.FloatingWindows,normal?SettingCategory.Normal:SettingCategory.Advanced,restart?SettingApplyMode.Restart:SettingApplyMode.Immediate,SettingPersistence.UserSettings,PreviewMode:preview?SettingPreviewMode.Reversible:SettingPreviewMode.None,DisplayNameKey:"Floating.Setting."+id,DescriptionKey:"Floating.Setting."+id+".Description");
    public Task<OperationResult> ValidateAsync(IReadOnlyDictionary<string,object?> values,CancellationToken cancellationToken=default)
    {
        foreach(var (size,min) in new[]{("floating.defaultCompactWidth","floating.minWidth"),("floating.defaultExpandedWidth","floating.minWidth"),("floating.defaultExpandedHeight","floating.minExpandedHeight")})
            if(Read(values,size,size.EndsWith("Height",StringComparison.Ordinal)?360:320)<Read(values,min,min.EndsWith("Height",StringComparison.Ordinal)?180:220))return Task.FromResult(OperationResult.Failure(new(ErrorCodes.ValidationFailed,"默认尺寸不得小于最小尺寸。",size)));
        return Task.FromResult(OperationResult.Success());
    }
    public Task ApplyAsync(IReadOnlyDictionary<string,object?> values,CancellationToken cancellationToken=default)=>apply(values);
    internal static int Read(IReadOnlyDictionary<string,object?> values,string key,int fallback)=>values.TryGetValue(key,out var value)&&value is not null?Convert.ToInt32(value):fallback;
}
