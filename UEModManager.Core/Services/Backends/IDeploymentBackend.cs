using System.Threading.Tasks;
using UEModManager.Models;

namespace UEModManager.Services.Backends
{
    /// <summary>
    /// 部署后端接口。
    /// 不同后端以不同方式将文件从仓库部署到游戏目录。
    ///
    /// 接口下沉 Core 的目的：让 <c>samples/UEModManager.SampleBackend/</c>
    /// 等第三方后端项目仅引用 Core 即可独立编译，无需拖入 WPF 依赖。
    /// </summary>
    public interface IDeploymentBackend
    {
        /// <summary>后端类型。</summary>
        DeploymentBackendType Type { get; }

        /// <summary>后端显示名称。</summary>
        string DisplayName { get; }

        /// <summary>
        /// 检测当前环境是否支持此后端。
        /// </summary>
        Task<bool> CanUseAsync();

        /// <summary>
        /// 部署单个文件：从源路径部署到目标路径。
        /// </summary>
        /// <param name="sourcePath">仓库中的源文件绝对路径。</param>
        /// <param name="targetPath">游戏目录中的目标绝对路径。</param>
        Task DeployFileAsync(string sourcePath, string targetPath);

        /// <summary>
        /// 移除已部署的文件。
        /// </summary>
        /// <param name="targetPath">游戏目录中的目标绝对路径。</param>
        Task RemoveFileAsync(string targetPath);
    }
}
