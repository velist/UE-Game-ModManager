using System;

namespace UEModManager.Services.Conflict.Model;

/// <summary>
/// 裁决共享组件时需要的、只能由调用方提供的信息。
///
/// 版本号来自 PE 文件的版本资源、修改时间来自文件系统 —— 都是 IO，
/// 因此 Core 只接收结果，不自己去读。
/// </summary>
/// <param name="Version">PE 文件版本（.dll/.exe），其他文件为 null。</param>
/// <param name="Modified">文件最后写入时间（UTC）。</param>
public sealed record SharedComponentProvenance(string? Version, DateTime? Modified);