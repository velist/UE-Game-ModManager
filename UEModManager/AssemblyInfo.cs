using System.Runtime.CompilerServices;
using System.Windows;

// ArchiveExtractor 等内部帮助类需要被单测直接驱动（嵌套解压的层数/体积上限是纯逻辑，
// 不该为了可测而把内部类型提升为公开 API）。
// 注意：本项目 GenerateAssemblyInfo=false，csproj 里的 <InternalsVisibleTo> 项不会生成，
// 必须写在这里。
[assembly: InternalsVisibleTo("UEModManager.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
