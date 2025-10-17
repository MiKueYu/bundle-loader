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
    [Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
    public class DreamoLoader : IOnLoad
    {
        private readonly ISptLogger<DreamoLoader> _logger;
        private readonly DatabaseService _databaseService;
        private readonly CustomItemService _customItemService;

        private string? _modPath;
        private const string DB_DIR = "db";
        private const string ITEMS_DIR = "items";


        public DreamoLoader(ISptLogger<DreamoLoader> logger, 
            DatabaseService databaseService, CustomItemService customItemService)
        {
            _logger = logger;
            _databaseService = databaseService;
            _customItemService = customItemService;
            _modPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _logger.Info($"[DreamoLoader] Mod路径: {_modPath}");
        }

        private void LoadItems()
        {
            if (_modPath == null) return;
            
            string itemsPath = Path.Combine(_modPath, DB_DIR, ITEMS_DIR);
            if (!Directory.Exists(itemsPath)) return;
            
            _logger.Info("[DreamoLoader] 开始加载物品数据");
            var itemFiles = Directory.GetFiles(itemsPath, "*.json");
            
            foreach (var itemFile in itemFiles)
            {
                LoadItemFromJson(itemFile);
            }
        }

        private void LoadItemFromJson(string itemFilePath)
        {
            string jsonContent = File.ReadAllText(itemFilePath);
            var itemJsonDoc = JsonDocument.Parse(jsonContent);
            var rootElement = itemJsonDoc.RootElement;
            
            string? itemId = GetJsonPropertyValue(rootElement, "_id");
            if (string.IsNullOrEmpty(itemId)) return;
            
            // 优先使用物品json中的 _proto 作为克隆模板，找不到再兜底
            string cloneTpl = GetJsonPropertyValue(rootElement, "_proto") ?? "66b37eb4acff495a29492407";
            _logger.Info($"[DreamoLoader] 使用模板克隆创建物品: {itemId}，模板: {cloneTpl}");
            CreateItemFromClone(itemId, cloneTpl, rootElement);
        }

        private void CreateItemFromClone(string newItemId, string cloneFrom, JsonElement rootElement)
        {
            // 使用确定性哈希方法生成24位ID，确保相同的输入始终产生相同的哈希值
            string hashedItemId = GenerateDeterministicHashId(newItemId);
            _logger.Info($"[DreamoLoader] 将ID转换为24位哈希: {newItemId} -> {hashedItemId}");
            
            var newItemDetails = new NewItemFromCloneDetails
            {
                ItemTplToClone = cloneFrom,
                NewId = hashedItemId,
                ParentId = GetJsonPropertyValue(rootElement, "parentId") ?? string.Empty,
                HandbookParentId = GetJsonPropertyValue(rootElement, "handbookParentId") ?? string.Empty,
                HandbookPriceRoubles = GetJsonPropertyDouble(rootElement, "handbookPrice"),
                Locales = GetLocalesFromJson(rootElement),
                // 针对每个物品单独选择 bundle
                OverrideProperties = CreatePropertiesOverride(rootElement, newItemId)
            };
            
            var result = _customItemService.CreateItemFromClone(newItemDetails);
            _logger.Info($"[DreamoLoader] 创建物品结果: {newItemId} - {(result?.Success ?? false ? "成功" : "失败")}");
        }
        
        /// <summary>
        /// 生成确定性的24位哈希ID，确保相同的输入始终产生相同的哈希值
        /// </summary>
        /// <param name="inputId">输入的原始ID</param>
        /// <returns>24位十六进制哈希字符串</returns>
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

        private TemplateItemProperties CreatePropertiesOverride(JsonElement rootElement, string itemId)
        {
            var propertiesOverride = new TemplateItemProperties();
            
            if (rootElement.TryGetProperty("_props", out var propsElement))
            {
                propertiesOverride.Name = propsElement.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() : null;
                propertiesOverride.Description = propsElement.TryGetProperty("Description", out var descEl) ? descEl.GetString() : null;
                
                // 为当前物品选择对应的 bundle key
                string bundlePath = GetBundlePathForItem(itemId, rootElement);
                if (!string.IsNullOrEmpty(bundlePath))
                {
                    propertiesOverride.Prefab = new Prefab
                    {
                        Path = bundlePath,
                        Rcid = ""
                    };
                    _logger.Info($"[DreamoLoader] 物品 {itemId} 绑定 bundle: {bundlePath}");
                }
            }
            
            return propertiesOverride;
        }
        
        /// <summary>
        /// 从bundles.json文件读取第一个bundle的key值
        /// </summary>
        /// <returns>bundle的路径</returns>
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
                        
                        if (root.TryGetProperty("manifest", out JsonElement manifestArray) && manifestArray.ValueKind == JsonValueKind.Array && manifestArray.GetArrayLength() > 0)
                        {
                            JsonElement firstBundle = manifestArray[0];
                            if (firstBundle.TryGetProperty("key", out JsonElement keyElement))
                            {
                                return keyElement.GetString() ?? "mods/tarkov_coin.bundle";
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
            return "mods/tarkov_coin.bundle";
        }

        /// <summary>
        /// 为指定物品选择合适的 bundle key，优先顺序：
        /// 1) item json 顶层的 "bundleKey"
        /// 2) item json 的 _props.BundleKey
        /// 3) bundles.json 中按 itemId 模糊匹配（文件名或路径片段包含 itemId）
        /// 4) 退回 bundles.json 的第一个 key，并记录警告日志
        /// </summary>
        private string GetBundlePathForItem(string itemId, JsonElement rootElement)
        {
            try
            {
                // 1) 顶层 bundleKey
                if (rootElement.TryGetProperty("bundleKey", out var topBundle) && topBundle.ValueKind == JsonValueKind.String)
                {
                    var v = topBundle.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) return v!;
                }

                // 2) _props.BundleKey
                if (rootElement.TryGetProperty("_props", out var propsEl) 
                    && propsEl.TryGetProperty("BundleKey", out var propBundle) 
                    && propBundle.ValueKind == JsonValueKind.String)
                {
                    var v = propBundle.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) return v!;
                }

                // 3) 在 bundles.json 中按 itemId 匹配
                string bundlesJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "bundles.json");
                if (File.Exists(bundlesJsonPath))
                {
                    string jsonContent = File.ReadAllText(bundlesJsonPath);
                    using var doc = JsonDocument.Parse(jsonContent);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("manifest", out var manifest) && manifest.ValueKind == JsonValueKind.Array)
                    {
                        string idLower = itemId.ToLowerInvariant();
                        foreach (var entry in manifest.EnumerateArray())
                        {
                            if (entry.TryGetProperty("key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
                            {
                                var key = keyEl.GetString() ?? string.Empty;
                                var fileNameNoExt = System.IO.Path.GetFileNameWithoutExtension(key).ToLowerInvariant();
                                var keyLower = key.ToLowerInvariant();

                                if (fileNameNoExt.Contains(idLower) || keyLower.Contains($"/{idLower}.") || keyLower.Contains($"/{idLower}/"))
                                {
                                    return key;
                                }
                            }
                        }

                        // 若未匹配，返回第一个 key 但给出警告
                        if (manifest.GetArrayLength() > 0)
                        {
                            var first = manifest[0];
                            if (first.TryGetProperty("key", out var firstKey))
                            {
                                var fallback = firstKey.GetString() ?? string.Empty;
                                _logger.Warning($"[DreamoLoader] 未在 bundles.json 为物品 {itemId} 找到匹配的bundle，回退到第一个key: {fallback}");
                                return fallback;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DreamoLoader] 为物品 {itemId} 选择bundle时出错，回退到默认。错误: {ex.Message}");
            }

            // 4) 最终兜底到原默认值
            return "mods/tarkov_coin.bundle";
        }

        private string? GetJsonPropertyValue(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var propElement))
            {
                if (propElement.ValueKind == JsonValueKind.String)
                {
                    return propElement.GetString();
                }
            }
            return null;
        }

        private double? GetJsonPropertyDouble(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var propElement))
            {
                if (propElement.TryGetDouble(out double value))
                {
                    return value;
                }
            }
            return null;
        }

        private Dictionary<string, LocaleDetails> GetLocalesFromJson(JsonElement rootElement)
        {
            var locales = new Dictionary<string, LocaleDetails>();
            
            // 获取物品ID以查找对应的本地化文件
            string? itemId = GetJsonPropertyValue(rootElement, "_id");
            if (itemId == null || _modPath == null) return locales;
            
            try
            {
                // 构建本地化文件路径
                string localeFilePath = Path.Combine(_modPath, DB_DIR, "locales", "itemsdescription", $"{itemId}.json");
                
                // 检查本地化文件是否存在
                if (File.Exists(localeFilePath))
                {
                    _logger.Info($"[DreamoLoader] 加载本地化文件: {localeFilePath}");
                    string localeContent = File.ReadAllText(localeFilePath);
                    var localeJsonDoc = JsonDocument.Parse(localeContent);
                    var localeRoot = localeJsonDoc.RootElement;
                    
                    // 提取本地化信息
                    string? name = localeRoot.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() : null;
                    string? shortName = localeRoot.TryGetProperty("ShortName", out var shortNameEl) ? shortNameEl.GetString() : name;
                    string? description = localeRoot.TryGetProperty("Description", out var descEl) ? descEl.GetString() : "";
                    
                    // 添加英文本地化（作为默认语言）
                    locales["en"] = new LocaleDetails
                    {
                        Name = name ?? itemId,
                        ShortName = shortName ?? name ?? itemId,
                        Description = description ?? ""
                    };
                    
                    _logger.Info($"[DreamoLoader] 成功加载物品 {itemId} 的本地化信息");
                }
                else
                {
                    _logger.Info($"[DreamoLoader] 未找到物品 {itemId} 的本地化文件: {localeFilePath}");
                    
                    // 如果没有找到本地化文件，使用物品ID作为名称
                    locales["en"] = new LocaleDetails
                    {
                        Name = itemId,
                        ShortName = itemId,
                        Description = string.Empty
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[DreamoLoader] 读取物品 {itemId} 的本地化信息时出错", ex);
                
                // 出错时使用物品ID作为默认值
                locales["en"] = new LocaleDetails
                {
                    Name = itemId,
                    ShortName = itemId,
                    Description = string.Empty
                };
            }
            
            return locales;
        }

        private void LoadBundleResources()
        {
            if (_modPath == null) return;
            
            _logger.Info("[DreamoLoader] 开始加载bundle资源");
            string bundlesPath = Path.Combine(_modPath, "bundles");
            
            if (!Directory.Exists(bundlesPath)) return;
            
            // 直接检查bundle文件是否存在
            string vssMagazineBundlePath = Path.Combine(bundlesPath, "mods", "tarkov_coin.bundle");
            
            if (File.Exists(vssMagazineBundlePath))
            {
                _logger.Info($"[DreamoLoader] 找到tarkov_coinbundle文件: {vssMagazineBundlePath}");
                // 简化实现，只记录日志，不实际加载bundle
                // bundle将通过SPT的mod系统自动加载，使用bundles.json中的配置
            }
            else
            {
                _logger.Error($"[DreamoLoader] 找不到tarkov_coinbundle文件: {vssMagazineBundlePath}");
            }
        }
        

        


        public async Task OnLoad()
        {
            _logger.Info("[DreamoLoader] 开始加载...");
            LoadItems();
            LoadBundleResources();
            _logger.Info("[DreamoLoader] 加载完成");
            await Task.CompletedTask;
        }
    }

    public record DreamoLoaderMetadata : AbstractModMetadata
    {
        public override string ModGuid { get; init; } = "DreamoLoader";
        public override string Name { get; init; } = "DreamoLoader";
        public override string Author { get; init; } = "Dreamo";
        public override SemanticVersioning.Version Version { get; init; } = SemanticVersioning.Version.Parse("1.0.0");
        public override SemanticVersioning.Range SptVersion { get; init; } = SemanticVersioning.Range.Parse("4.0.0");
        public override List<string>? Contributors { get; init; } = new List<string>();
        public override string? Url { get; init; } = "";
        public override string License { get; init; } = "MIT";
        public override bool? IsBundleMod { get; init; } = true;
        public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new Dictionary<string, SemanticVersioning.Range>();
        public override List<string>? Incompatibilities { get; init; } = new List<string>();
    }
}