# DreamoLoader 教学文档

## 所需开发环境

要开发和编译 DreamoLoader mod，您需要以下环境配置：

1. **Visual Studio 版本要求**：
   - Visual Studio 2022（推荐最新版本）
   - 安装 ".NET 桌面开发" 工作负载
   - 安装 ".NET 9.0 SDK"（项目使用 net9.0 框架）

2. **.NET 版本**：
   - .NET 9.0 SDK（可从 [Microsoft 官方网站](https://dotnet.microsoft.com/download) 下载）

3. **必要的引用库**：
   - **NuGet 包引用**：
     - SPTarkov.Common (4.0.0)
     - SPTarkov.DI (4.0.0)
     - SPTarkov.Reflection (4.0.0)
     - SPTarkov.Server.Core (4.0.0)
     
   - **本地 DLL 引用**（路径位于 SPT\ 目录下）：
     - SPT.Server.dll
     - SPT.Server.Linux.dll
     - SPTarkov.Common.dll
     - SPTarkov.DI.dll
     - SPTarkov.Reflection.dll
     - SPTarkov.Server.Assets.dll
     - SPTarkov.Server.Core.dll
     - SPTarkov.Server.Web.dll
     - System.IO.Hashing.dll
     - SemanticVersioning.dll

4. **编译命令**：
   ```bash
   dotnet build DreamoLoader.csproj --configuration Release
   ```

5. **SPT-AKI 版本兼容性**：
   - 本项目针对 SPT-AKI 4.0.0 版本开发


## 目录

1. [Mod 结构概述](#mod-结构概述)
2. [加载独立物品的实现](#加载独立物品的实现)
3. [Bundle 资源加载机制](#bundle-资源加载机制)
4. [确定性哈希 ID 生成](#确定性哈希-id-生成)
5. [完整代码分析与注释](#完整代码分析与注释)
6. [配置文件详解](#配置文件详解)
7. [开发要点与注意事项](#开发要点与注意事项)

## Mod 结构概述

DreamoLoader mod 的基本结构如下：

```
DreamoLoader/
├── DreamoLoader.cs       # 主源码文件
├── DreamoLoader.csproj   # 项目配置文件
├── mod.json              # Mod 元数据配置
├── bundles.json          # Bundle 资源配置
├── db/                   # 数据库文件目录
│   ├── items/            # 物品定义文件
│   └── locales/          # 本地化文件
└── bundles/              # Bundle 资源文件
    └── mods/             # 模型资源
        └── magazines/    # 弹匣模型
```

## 加载独立物品的实现

### 物品加载流程

1. **初始化与依赖注入**
2. **扫描物品配置文件**
3. **创建物品实例**
4. **设置物品属性与本地化**

### 核心代码解析

```csharp
// 物品加载的主入口方法
private void LoadItems()
{
    if (_modPath == null) return;
    
    // 构建物品配置文件的路径
    string itemsPath = Path.Combine(_modPath, DB_DIR, ITEMS_DIR);
    if (!Directory.Exists(itemsPath)) return;
    
    _logger.Info("[DreamoLoader] 开始加载物品数据");
    // 获取所有 JSON 格式的物品配置文件
    var itemFiles = Directory.GetFiles(itemsPath, "*.json");
    
    // 逐个加载物品
    foreach (var itemFile in itemFiles)
    {
        LoadItemFromJson(itemFile);
    }
}
```

### 物品实例创建

DreamoLoader 使用克隆现有物品的方式来创建新物品，这样可以继承原有物品的基本属性和行为：

```csharp
private void CreateItemFromClone(string newItemId, string cloneFrom, JsonElement rootElement)
{
    // 使用确定性哈希方法生成24位ID，确保相同的输入始终产生相同的哈希值
    string hashedItemId = GenerateDeterministicHashId(newItemId);
    _logger.Info($"[DreamoLoader] 将ID转换为24位哈希: {newItemId} -> {hashedItemId}");
    
    // 创建新物品详情对象
    var newItemDetails = new NewItemFromCloneDetails
    {
        ItemTplToClone = cloneFrom,    // 指定要克隆的模板物品ID
        NewId = hashedItemId,          // 设置新物品的ID
        ParentId = GetJsonPropertyValue(rootElement, "parentId") ?? string.Empty,
        HandbookParentId = GetJsonPropertyValue(rootElement, "handbookParentId") ?? string.Empty,
        HandbookPriceRoubles = GetJsonPropertyDouble(rootElement, "handbookPrice"),
        Locales = GetLocalesFromJson(rootElement),             // 获取本地化信息
        OverrideProperties = CreatePropertiesOverride(rootElement)  // 设置要覆盖的属性
    };
    
    // 使用SPT的CustomItemService创建物品
    var result = _customItemService.CreateItemFromClone(newItemDetails);
    _logger.Info($"[DreamoLoader] 创建物品结果: {newItemId} - {(result?.Success ?? false ? "成功" : "失败")}");
}
```

## Bundle 资源加载机制

### Bundle 配置与加载原理

SPT-AKI 使用 `bundles.json` 文件来配置需要加载的 Unity bundle 资源。每个 bundle 可以指定其依赖关系，确保加载顺序正确。

### 从 bundles.json 读取配置

DreamoLoader 实现了一个方法来读取 bundles.json 文件中的配置信息：

```csharp
private string GetBundlePathFromConfig()
{
    try
    {
        string bundlesJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "bundles.json");
        
        if (File.Exists(bundlesJsonPath))
        {
            string jsonContent = File.ReadAllText(bundlesJsonPath);
            using (JsonDocument doc = JsonDocument.Parse(jsonContent))
            {
                JsonElement root = doc.RootElement;
                
                // 读取manifest数组中的第一个bundle配置
                if (root.TryGetProperty("manifest", out JsonElement manifestArray) && 
                    manifestArray.ValueKind == JsonValueKind.Array && 
                    manifestArray.GetArrayLength() > 0)
                {
                    JsonElement firstBundle = manifestArray[0];
                    if (firstBundle.TryGetProperty("key", out JsonElement keyElement))
                    {
                        return keyElement.GetString() ?? "mods/magazines/mag_vss_tochmash_sr3m130_9x39_30.bundle";
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"读取bundles.json失败: {ex.Message}");
    }
    
    // 读取失败时使用默认值
    return "mods/magazines/mag_vss_tochmash_sr3m130_9x39_30.bundle";
}
```

### 配置物品的 Prefab 属性

在 `CreatePropertiesOverride` 方法中，我们将从 bundles.json 读取到的路径设置到物品的 Prefab 属性中：

```csharp
private TemplateItemProperties CreatePropertiesOverride(JsonElement rootElement)
{
    var propertiesOverride = new TemplateItemProperties();
    
    if (rootElement.TryGetProperty("_props", out var propsElement))
    {
        propertiesOverride.Name = propsElement.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() : null;
        propertiesOverride.Description = propsElement.TryGetProperty("Description", out var descEl) ? descEl.GetString() : null;
        
        // 从bundles.json文件读取bundle key
        string bundlePath = GetBundlePathFromConfig();
        if (!string.IsNullOrEmpty(bundlePath))
        {
            propertiesOverride.Prefab = new Prefab
            {
                Path = bundlePath,  // 这里必须与bundles.json中的key完全匹配，包括.bundle扩展名
                Rcid = ""
            };
        }
    }
    
    return propertiesOverride;
}
```

**重要提示**：Prefab.Path 必须与 bundles.json 中的 key 完全匹配，包括 `.bundle` 扩展名，否则物品的模型将无法正确加载。

## 确定性哈希 ID 生成

为了确保相同的输入 ID 始终生成相同的哈希值，DreamoLoader 实现了一个确定性哈希 ID 生成方法：

```csharp
private string GenerateDeterministicHashId(string inputId)
{
    using (var sha256 = System.Security.Cryptography.SHA256.Create())
    {
        // 将输入ID转换为字节数组并计算哈希值
        byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(inputId);
        byte[] hashBytes = sha256.ComputeHash(inputBytes);
        
        // 取前12个字节（24个十六进制字符）作为MongoId兼容的哈希值
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < Math.Min(12, hashBytes.Length); i++)
        {
            sb.Append(hashBytes[i].ToString("x2"));
        }
        
        // 确保返回24个字符，如果不够则用0填充
        string hashString = sb.ToString();
        if (hashString.Length < 24)
        {
            hashString = hashString.PadRight(24, '0');
        }
        
        return hashString;
    }
}
```

这个方法使用 SHA256 哈希算法，确保相同的输入 ID 始终生成相同的 24 位哈希值，适用于需要稳定 ID 的场景。

## 完整代码分析与注释

以下是带有详细中文注释的 DreamoLoader.cs 代码核心部分：

```csharp
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SemanticVersioning;
using SPTarkov.DI;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Logging;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Templates;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Mod;

using Path = System.IO.Path;

namespace DreamoLoader
{
    // 注册此类为可注入服务，并设置加载优先级
    [Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
    public class DreamoLoader : IOnLoad
    {
        // 依赖注入的服务
        private readonly ISptLogger<DreamoLoader> _logger;           // 日志服务
        private readonly DatabaseService _databaseService;           // 数据库服务
        private readonly CustomItemService _customItemService;       // 自定义物品服务

        private string? _modPath;                                    // Mod 路径
        private const string DB_DIR = "db";                          // 数据库目录名
        private const string ITEMS_DIR = "items";                    // 物品目录名

        // 构造函数，通过依赖注入获取服务实例
        public DreamoLoader(ISptLogger<DreamoLoader> logger, 
            DatabaseService databaseService, CustomItemService customItemService)
        {
            _logger = logger;
            _databaseService = databaseService;
            _customItemService = customItemService;
            // 获取当前程序集的目录作为mod路径
            _modPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _logger.Info($"[DreamoLoader] Mod路径: {_modPath}");
        }

        // 加载所有物品数据
        private void LoadItems()
        {}

        // 从JSON文件加载单个物品
        private void LoadItemFromJson(string itemFilePath)
        {}

        // 通过克隆现有物品创建新物品
        private void CreateItemFromClone(string newItemId, string cloneFrom, JsonElement rootElement)
        {}
        
        // 生成确定性哈希ID
        private string GenerateDeterministicHashId(string inputId)
        {}

        // 创建物品属性覆盖对象
        private TemplateItemProperties CreatePropertiesOverride(JsonElement rootElement)
        {}
        
        // 从bundles.json读取bundle路径
        private string GetBundlePathFromConfig()
        {}

        // 获取JSON属性值的辅助方法
        private string? GetJsonPropertyValue(JsonElement element, string propertyName)
        {}

        private double? GetJsonPropertyDouble(JsonElement element, string propertyName)
        {}

        // 从JSON文件加载本地化信息
        private Dictionary<string, LocaleDetails> GetLocalesFromJson(JsonElement rootElement)
        {}

        // 加载bundle资源
        private void LoadBundleResources()
        {}

        // 实现IOnLoad接口，游戏加载时调用
        public async Task OnLoad()
        {
            _logger.Info("[DreamoLoader] 开始加载...");
            // 执行物品加载和bundle加载
            LoadItems();
            LoadBundleResources();
            _logger.Info("[DreamoLoader] 加载完成");
            await Task.CompletedTask;
        }
    }

    // Mod元数据类，定义Mod的基本信息
    public record DreamoLoaderMetadata : AbstractModMetadata
    {}
}
```

## 配置文件详解

### mod.json 配置

`mod.json` 文件定义了 Mod 的基本信息和入口点：

```json
{
  "name": "DreamoLoader",
  "version": "1.0.0",
  "author": "Dreamo",
  "description": "加载独立物品的mod",
  "entryPoint": "DreamoLoader.dll",
  "entryClass": "DreamoLoader.DreamoLoader",
  "entryMethod": "OnLoad",
  "isServerMod": true,
  "sptVersion": "4.0.0"
}
```

**参数说明**：
- `name`：Mod 名称
- `version`：Mod 版本
- `author`：作者
- `description`：描述
- `entryPoint`：DLL 文件名
- `entryClass`：入口类的完全限定名
- `entryMethod`：入口方法名
- `isServerMod`：是否为服务器 Mod
- `sptVersion`：兼容的 SPT 版本

### bundles.json 配置

`bundles.json` 文件定义了需要加载的 Unity bundle 资源及其依赖：

```json
{
  "manifest": [
    {
      "key": "mods/magazines/mag_vss_tochmash_sr3m130_9x39_30.bundle",
      "dependencyKeys": [
        "cubemaps",
        "shaders"
      ]
    }
  ]
}
```

**参数说明**：
- `manifest`：bundle 配置数组
- `key`：bundle 文件路径，**必须与 Prefab.Path 完全匹配**
- `dependencyKeys`：依赖的其他 bundle 列表

## 开发要点与注意事项

1. **Prefab.Path 必须与 bundles.json 中的 key 完全匹配**，包括 `.bundle` 扩展名
2. **确保正确设置 bundle 依赖**，
3. **使用确定性哈希 ID** 可以确保物品 ID 的一致性
4. **正确处理本地化**，为物品提供名称和描述
5. **设置合适的加载优先级**，确保在数据库加载后加载自定义物品
6. **错误处理和日志记录**对于调试非常重要

## 常见问题排查

1. **模型不显示**：检查 Prefab.Path 是否与 bundles.json 中的 key 完全匹配
2. **加载失败**：检查 bundle 依赖是否正确设置
3. **物品属性异常**：检查克隆模板是否合适，属性覆盖是否正确
4. **本地化不生效**：检查本地化文件路径和格式是否正确

通过遵循本教程，您可以成功创建自己的物品加载器，为 SPT-AKI 添加自定义物品和模型。