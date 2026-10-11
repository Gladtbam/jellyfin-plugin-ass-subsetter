using System.Reflection;
using Jellyfin.Plugin.AssSubsetter.Configuration;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Configuration;

public class ConfigPageTests
{
    [Fact]
    public void EmbeddedConfigPage_ShouldReferenceRegisteredExternalScript()
    {
        var html = ReadResource("Jellyfin.Plugin.AssSubsetter.Configuration.configPage.html");
        Assert.Contains("data-controller=\"__plugin/ass-subsetter-config.js\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.NotNull(typeof(PluginConfiguration).Assembly.GetManifestResourceStream("Jellyfin.Plugin.AssSubsetter.Configuration.configPage.js"));
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldContainGenerateMksOptionAndLocalizedLabels()
    {
        var html = GetConfigPageAssets();

        Assert.Contains("<option value=\"2\" data-i18n=\"optGenerateMks\">Generate MKS (Experimental)</option>", html, StringComparison.Ordinal);
        Assert.Contains("生成 MKS（实验性）", html, StringComparison.Ordinal);
        Assert.Contains("Generate MKS (Experimental)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldNormalizeGenerateMksSerializedValues()
    {
        var html = GetConfigPageAssets();

        Assert.Contains("modeVal === 'GenerateMks' || modeVal === 2", html, StringComparison.Ordinal);
        Assert.Contains("modeVal = '2';", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldShowSettingsAndFontManagementAsTwoMainTabs()
    {
        var html = GetConfigPageAssets();

        Assert.Contains("id=\"GeneralSettingsTab\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"FontManagementTab\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"GeneralSettingsPanel\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"FontManagementPanel\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"CustomFontDirectories\"", html, StringComparison.Ordinal);
        Assert.Contains("data-font-view=\"analysis\"", html, StringComparison.Ordinal);
        Assert.Contains("字体管理", html, StringComparison.Ordinal);
        Assert.DoesNotContain("FontCacheFilePath", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldUseReadableRaisedButtonsForFontManagementNavigation()
    {
        var html = GetConfigPageAssets();
        string[] buttonIds =
        [
            "ReportPreviousPage", "ReportNextPage",
            "QuarantinePreviousPage", "QuarantineNextPage", "RefreshQuarantineButton",
            "RenameItemsPreviousPage", "RenameItemsNextPage",
            "NormalizeSelectedButton", "RollbackSelectedButton"
        ];

        foreach (string buttonId in buttonIds)
        {
            Assert.Contains($"id=\"{buttonId}\" type=\"button\" is=\"emby-button\" class=\"raised emby-button\"", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldPreserveAllGeneralSettingsLoadAndSaveBindings()
    {
        var html = GetConfigPageAssets();
        string[] settingNames =
        [
            "SubtitleMode",
            "CustomFontDirectories",
            "ProtectedFontDirectories",
            "PreferredFontDirectories",
            "MaxCacheSizeMB",
            "FontHistoryRetentionCount",
            "EnableAutoScanProcessing",
            "EnablePrefetchSubsetting",
            "PrefetchTriggerPercent",
            "FallbackToOriginalOnError",
            "AssToSupFrameRate"
        ];

        foreach (string settingName in settingNames)
        {
            Assert.Contains($"id=\"{settingName}\"", html, StringComparison.Ordinal);
            Assert.Contains($"config.{settingName}", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldDefaultToNoSelectedQuarantineItems()
    {
        var html = GetConfigPageAssets();

        Assert.Contains("selectedFileIds: new Set()", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldUseBoundedServerPagingAndPollPersistedOperations()
    {
        var html = GetConfigPageAssets();

        Assert.Contains("pageSize: 50", html, StringComparison.Ordinal);
        Assert.Contains("Math.min(200", html, StringComparison.Ordinal);
        Assert.Contains("operations/" , html, StringComparison.Ordinal);
        Assert.Contains("pollOperation", html, StringComparison.Ordinal);
        Assert.Contains("operationStatus", html, StringComparison.Ordinal);
        Assert.Contains("authenticatedFetch('export'", html, StringComparison.Ordinal);
        Assert.Contains("{ analysisBatchId: batchId }", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldWireEveryFontMaintenanceActionToTheTaskApi()
    {
        var html = GetConfigPageAssets();
        string[] operationRoutes =
        [
            "operations/analyze",
            "operations/quarantine",
            "operations/quarantine/all-verified",
            "operations/restore",
            "operations/purge",
            "operations/normalize-names",
            "operations/rename-rollback",
            "operations/rebuild-derived-data"
        ];

        foreach (string operationRoute in operationRoutes)
        {
            Assert.Contains(operationRoute, html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldUseExactQuarantineCandidateQueryFilter()
    {
        var html = GetConfigPageAssets();

        Assert.Contains("decision: 'quarantineCandidate'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("decision: 'QuarantineCandidate'", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldExposeOverviewDiagnosticsAndEveryAnalysisReportFilter()
    {
        var html = GetConfigPageAssets();

        string[] overviewBindings =
        [
            "publishedGenerationId",
            "eligibleQuarantineCount",
            "reclaimableBytes",
            "databaseHealth",
            "phaseTimings",
            "FontDatabaseSummary",
            "FontDatabaseValues",
            "FontPerformanceRows"
        ];
        string[] reportFilters =
        [
            "ReportRelationshipFilter",
            "ReportUnclassifiableFilter",
            "ReportDirectoryFilter",
            "ReportTechnologyFilter",
            "ReportActionFilter",
            "analysis-report-rows"
        ];

        Assert.All(overviewBindings, binding => Assert.Contains(binding, html, StringComparison.Ordinal));
        Assert.DoesNotContain("id=\"FontDatabaseTab\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-font-view=\"database\"", html, StringComparison.Ordinal);
        Assert.All(reportFilters, filter => Assert.Contains(filter, html, StringComparison.Ordinal));
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldBindAllVerifiedConfirmationBodyToItsRoute()
    {
        var html = GetConfigPageAssets();
        string call = GetOperationCall(html, "operations/quarantine/all-verified");

        Assert.Contains("batchId: batchId", call, StringComparison.Ordinal);
        Assert.Contains("confirmAllVerified: true", call, StringComparison.Ordinal);
        Assert.Contains("idempotencyKey: createIdempotencyKey()", call, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("operations/restore")]
    [InlineData("operations/normalize-names")]
    [InlineData("operations/rename-rollback")]
    public void EmbeddedConfigPage_ShouldBindMaintenanceSelectionBodyToItsRoute(string route)
    {
        var html = GetConfigPageAssets();
        string call = GetOperationCall(html, route);

        if (route == "operations/normalize-names")
        {
            Assert.Contains("generationId: context.generationId", call, StringComparison.Ordinal);
            Assert.Contains("analysisBatchId: context.analysisBatchId", call, StringComparison.Ordinal);
            Assert.Contains("ruleFingerprint: context.ruleFingerprint", call, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("batchId:", call, StringComparison.Ordinal);
        }
        Assert.Contains(route == "operations/restore" ? "selectedFileIds: fileIds" : "selectedFileIds: selected", call, StringComparison.Ordinal);
        Assert.Contains("idempotencyKey: createIdempotencyKey()", call, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldConfirmAllVerifiedCandidatesWithBatchCountAndBytes()
    {
        var html = GetConfigPageAssets();

        Assert.Contains("QuarantineCandidate", html, StringComparison.Ordinal);
        Assert.Contains("candidateFileIds.size", html, StringComparison.Ordinal);
        Assert.Contains("confirmAllVerified", html, StringComparison.Ordinal);
        Assert.Contains("batchId", html, StringComparison.Ordinal);
        Assert.Contains("itemCount", html, StringComparison.Ordinal);
        Assert.Contains("bytes", html, StringComparison.Ordinal);
        Assert.Contains("隔离全部已验证项", html, StringComparison.Ordinal);
        Assert.Contains("Quarantine all verified", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldRenderDynamicValuesWithoutHtmlInterpolation()
    {
        var html = GetConfigPageAssets();

        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);
        Assert.Contains("textContent", html, StringComparison.Ordinal);
        Assert.Contains("createTextCell", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldDistinguishUnavailableProofFromAbsentOptionalFeatures()
    {
        string html = GetConfigPageAssets();

        Assert.Contains("['Unknown', 'Passed', 'Failed', 'NotApplicable']", html, StringComparison.Ordinal);
        Assert.Contains("无法验证", html, StringComparison.Ordinal);
        Assert.Contains("不适用", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldRenderCompleteRelationshipExplanation()
    {
        string html = GetConfigPageAssets();

        Assert.All(new[] { "differingCodePoints", "differenceTypes", "leftCanReplaceRight", "rightCanReplaceLeft", "collectionFaceImpact", "retentionScore", "scoreComponents", "retainedRationale", "renderReportEvidence", "relationshipLabels", "actionLabels" },
            value => Assert.Contains(value, html, StringComparison.Ordinal));
        Assert.Contains("className = 'fontPathGrid'", html, StringComparison.Ordinal);
        Assert.Contains("className = 'fontScoreGrid'", html, StringComparison.Ordinal);
        Assert.Contains("#ReportRelationshipFilter option", html, StringComparison.Ordinal);
        Assert.Contains("#ReportActionFilter option", html, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldRenderDuplicateGroupAsFileCardsAndExpandablePairProof()
    {
        string html = GetConfigPageAssets();

        Assert.Contains("function renderGroupDetail(detail, target)", html, StringComparison.Ordinal);
        Assert.Contains("fontGroupFileGrid", html, StringComparison.Ordinal);
        Assert.Contains("fontGroupPairList", html, StringComparison.Ordinal);
        Assert.Contains("localizeMemberReason(member.reason)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"FontAnalysisRows\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"FontGroupDetail\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"ReportSelectionStatus\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("[summary].concat(pairDetails, memberDetails)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldUseSourceSelectionsAndServerStatusPaging()
    {
        string html = GetConfigPageAssets();

        Assert.Contains("id=\"RenameItemsPreviousPage\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"RenameItemsNextPage\"", html, StringComparison.Ordinal);
        Assert.Contains("authenticatedFetch('rename/items'", html, StringComparison.Ordinal);
        Assert.Contains(".fontRenameCard--selected", html, StringComparison.Ordinal);
        Assert.Contains("id=\"RenameStatus-pending\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"RenameStatus-skipped\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"RenameStatus-failed\"", html, StringComparison.Ordinal);
        Assert.Contains("fontManagementState.renameFileIds.clear();", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NamingHelpUsesServerCatalogAndOnlyTextFormatting()
    {
        string assets = GetConfigPageAssets();
        Assert.Contains("fontManagementState.namingFields", assets, StringComparison.Ordinal);
        Assert.Contains("formatVersion: 2", assets, StringComparison.Ordinal);
        Assert.Contains("{upper(family)}", assets, StringComparison.Ordinal);
        Assert.Contains("{lower(style)}", assets, StringComparison.Ordinal);
        Assert.DoesNotContain("FontNamingAdvancedHelpContent", assets, StringComparison.Ordinal);
        Assert.DoesNotContain("isCollection", assets, StringComparison.Ordinal);
        Assert.DoesNotContain("resolvedName", assets, StringComparison.Ordinal);
        Assert.DoesNotContain("collisionVersion", assets, StringComparison.Ordinal);
        Assert.Contains("Invalid Unicode", assets, StringComparison.Ordinal);
        Assert.Contains("无效 Unicode", assets, StringComparison.Ordinal);
    }
    private static string GetConfigPageAssets()
    {
        return ReadResource("Jellyfin.Plugin.AssSubsetter.Configuration.configPage.html")
            + Environment.NewLine
            + ReadResource("Jellyfin.Plugin.AssSubsetter.Configuration.configPage.js");
    }

    private static string ReadResource(string name)
    {
        var assembly = typeof(PluginConfiguration).Assembly;
        using var stream = assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    private static string GetOperationCall(string html, string route)
    {
        string marker = $"queueOperation('{route}', {{";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Operation call for route '{route}' was not found.");
        int bodyEnd = html.IndexOf('}', start + marker.Length);
        Assert.True(bodyEnd > start, $"Operation body for route '{route}' was not closed.");
        return html[start..(bodyEnd + 1)];
    }
    [Fact]
    public void RenamePageUsesCurrentStatusPagingInsteadOfPlanPublication()
    {
        string assets = GetConfigPageAssets();
        Assert.DoesNotContain("RenameBatchSelect", assets, StringComparison.Ordinal);
        Assert.Contains("rename/items", assets, StringComparison.Ordinal);
        Assert.Contains("operations/normalize-names", assets, StringComparison.Ordinal);
        Assert.Contains("staleReasons", assets, StringComparison.Ordinal);
    }

}
