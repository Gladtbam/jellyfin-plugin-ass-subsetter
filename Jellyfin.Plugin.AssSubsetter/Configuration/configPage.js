var AssSubsetterConfig = {
    pluginUniqueId: '7d13aa46-8b4a-ce85-9648-2cf4f52b8222'
};

var i18nStrings = {
    zh: {
        subtitleMode: '字幕处理模式',
        customFontDirs: '自定义源字体目录',
        protectedFontDirs: '受保护字体目录',
        preferredFontDirs: '优先字体目录',
        scanFrameRate: '扫描帧率',
        maxCacheSize: '最大缓存容量限制 (MB)',
        fontHistoryRetention: '分析报告及发布代次保留数量',
        enableAutoScan: '开启自动扫描预处理',
        enablePrefetch: '开启播放时提前处理下一集',
        prefetchTrigger: '提前处理触发进度',
        fallbackOnError: '异常时回退至原版字幕',
        saveBtn: '保存',
        optSubsetting: '字体子集化',
        optConvertSup: '转换为 SUP 位图字幕',
        optGenerateMks: '生成 MKS（实验性）',
        descSubtitleMode: '选择字幕处理方式。MKS 生成为实验性功能，面向 mpv 等兼容播放器，必须手动启用，不会根据客户端自动选择。各模式互斥。',
        descCustomFontDirs: '例如：/fonts/my_anime_fonts;/media/extra_fonts。多个路径请用分号 ; 隔开。(系统默认字体目录已被自动包含，无需填写)',
        descProtectedFontDirs: '重复字体清理和重命名永远不得修改的附加目录。',
        descPreferredFontDirs: '运行时查找和重复项保留时优先选择的目录。',
        descScanFrameRate: 'ASS 转 SUP 时的渲染采样帧率。值越高动画越流畅但文件越大（推荐值: 24）。视频分辨率从元数据自动获取。',
        descMaxCacheSize: '处理后的临时字幕缓存文件的最大总体积。达到上限后将自动清理最旧的缓存（推荐值: 1024）。',
        descFontHistoryRetention: '仅保留最近 N 批分析报告及对应发布代次；填 0 表示全部保留。当前库存、进行中操作及有效重命名计划所需数据会受到保护。',
        descAutoScan: '开启后，媒体库添加入库新视频时将自动在后台预处理字幕缓存。',
        descPrefetch: '开启后，当检测到用户播放进度接近当前剧集结尾时，自动在后台提前处理下一集的所有 ASS 字幕。',
        descPrefetchTrigger: '当播放进度达到此百分比时触发下一集的提前处理（推荐值: 85）。',
        descFallback: '开启后，如果处理失败将无缝返回原始字幕，确保播放不中断。',
        generalSettingsTab: '常规设置',
        fontManagementTab: '字体管理',
        fontOverviewTab: '概览',
        fontAnalysisTab: '分析报告',
        fontQuarantineTab: '隔离区',
        fontRenameTab: '文件名重命名',
        fontDatabaseTab: '数据库健康',
        overviewIntro: '查看已发布字体、最新分析结果、数据库健康和近期处理耗时。',
        overviewScanChanges: '上次扫描变更原因',
        overviewAnalysisValidity: '分析结论有效性',
        analysisCurrent: '分析有效',
        analysisStale: '分析已过期',
        renameIntro: '核对当前文件的源路径、目标路径和证据，选择待处理项执行。',
        databaseIntro: '查看数据库状态和近期字体处理耗时。',
        analyzeFonts: '分析字体',
        exportReport: '导出报告',
        analysisBatch: '分析批次',
        quarantineIntro: '按文件当前状态查看隔离结果；已隔离文件可选择恢复。',
        quarantineStatusFilter: '隔离状态',
        refreshQuarantine: '刷新隔离区',
        quarantineDone: '已隔离',
        quarantineFailed: '隔离失败',
        quarantineWaiting: '待隔离',
        quarantinePath: '隔离存放路径',
        quarantineTargetPath: '目标隔离路径',
        quarantinePermissionDenied: 'Jellyfin 无法移动或移除源字体。请检查源文件所在目录是否允许 jellyfin 用户写入。',
        quarantineIoFailure: '文件操作失败',
        renameBatch: '重命名计划或批次',
        renameHistory: '重命名历史',
        renameStatusFilter: '项目状态',
        renamePendingFilter: '待处理',
        refreshRenameCandidates: '刷新重命名候选',
        renameCandidatesPending: '等待生成重命名候选',
        renameCandidatesRunning: '正在生成重命名候选',
        renameCandidatesReady: '重命名候选已生成',
        renameCandidatesStale: '重命名候选已过期',
        renameCandidatesFailed: '重命名候选生成失败',
        renameCandidatesGenerated: '上次生成：{0}',
        renameCandidatesNeedAnalysis: '请先完成字体扫描与分析',
        renameFailedFilter: '失败', renameRenamedFilter: '已重命名', renameRolledbackFilter: '已回滚',
        publicationHelp: '仅扫描完整成功且文件、目录策略或分析证据版本发生变化，才发布新代次和新分析批次；无变化沿用当前值，失败或取消不发布。重命名不发布计划批次。',
        staleReasonLabels: { 'files-changed': '文件已变化', 'analysis-incomplete': '分析未完成', 'generation-superseded': '源代次已被替代', 'analysis-superseded': '分析批次已被替代', 'directory-policy-changed': '目录策略已变化', 'evidence-version-changed': '分析证据版本已变化', 'initial-scan': '首次扫描' },
        lastScanChanged: '上次扫描：发布新代次和分析', lastScanUnchanged: '上次扫描：无变化，沿用代次和分析',

        renameSkippedFilter: '已跳过',
        analysisIntro: '可隔离文件按整文件替代方案合并展示；冲突与覆盖差异保留独立报告。展开卡片查看字体比对与评分依据。',
        replacementTitle: '已验证可隔离文件',
        replacementSummary: '{0} 个保留文件 · {1} 个可隔离文件',
        retainedFilesTitle: '保留文件',
        candidateFilesTitle: '可隔离文件',
        replacementEvidence: '展开字体比对与评分依据',
        selectedFilesStatus: '已选 {0} 个文件（包含其他页的选择）',
        clearSelection: '清空选择',
        selectPlan: '选择本方案',
        deselectPlan: '取消选择本方案',
        moreFiles: '展开另外 {0} 个文件',
        batchColumn: '批次',
        kindColumn: '类型',
        selectColumn: '选择',
        groupColumn: '分组',
        fileColumn: '文件 ID',
        faceColumn: '字体面',
        decisionColumn: '决定',
        scoreColumn: '分数',
        reasonColumn: '原因',
        actionsColumn: '操作',
        statusColumn: '状态',
        expiryColumn: '到期时间',
        errorColumn: '错误',
        oldPathColumn: '原路径',
        newPathColumn: '目标路径',
        previousPage: '上一页',
        nextPage: '下一页',
        jumpToPage: '跳转页',
        pageNumber: '第 {0} 页',
        quarantineSelected: '隔离所选',
        quarantineAllVerified: '隔离全部已验证项',
        restoreSelected: '恢复所选字体',
        purgeExpired: '清除过期项',
        normalizeSelected: '重命名所选',
        rollbackSelected: '回滚所选',
        performanceSamples: '性能样本',
        phaseColumn: '阶段',
        durationColumn: '耗时',
        itemsColumn: '项目数',
        reusedColumn: '复用数',
        recordedColumn: '记录时间',
        rebuildDerivedData: '重建派生数据',
        loadingState: '正在加载…',
        emptyState: '没有可显示的记录。',
        errorState: '加载失败：{0}',
        selectRequired: '请先选择至少一个项目。',
        operationQueued: '操作请求 #{0} 已持久化，当前状态：{1}',
        operationStatus: '操作请求 #{0}：{1}',
        operationFailed: '操作请求 #{0}：{1}。{2}',
        pageStatus: '第 {0} 页，共 {1} 条',
        groupDetails: '分组 #{0}：约束 {1}；跳过原因 {2}；证据关系 {3}',
        groupHeading: '分组 #{0} · {1} 个文件 · {2} 个 Face',
        groupFilesTitle: '组内文件',
        groupPairsTitle: '组内比对 · {0} 条',
        groupOtherPairs: '其他组内比对 · {0} 条',
        groupTechnicalTitle: '判定依据与保留原因',
        groupConstraintsTitle: '判定条件',
        groupReasonsTitle: '保留原因',
        groupFacesLabel: '包含的 Face',
        groupFaceCount: '{0} 个 Face',
        groupReclaimable: '可回收 {0}',
        groupPairLabel: 'Face #{0} 与 Face #{1}',
        groupNoPairs: '没有保存组内比对证据。',
        reasonQuality: '按质量评分和稳定顺序保留。',
        reasonDirectReplacement: '已由保留文件 #{0} 直接替代。',
        reasonProtected: '所在目录受保护，必须保留。',
        reasonLinked: '存在关联路径，不参与清理。',
        reasonIncomplete: '分类证据不完整，必须保留。',
        collectionImpactText: '文件 #{0} 包含 {1} 个 Face；同一文件中还有 {2} 个 Face。',
        constraintLabels: {
            'classification.incomplete': '分类证据不完整', 'coverage.intersection': '字符覆盖相交', 'coverage.superset': '字符覆盖为超集',
            'face.index': '集合内 Face 索引', 'glyphs.incomplete': '字形证据不完整', 'identity.independent': '字体标识独立',
            'identity.same': '字体标识相同', 'name.postscript': 'PostScript 名称', 'semantics.all': '语义内容一致',
            'semantics.conflict': '语义内容冲突', 'semantics.hinting': '字形微调', 'semantics.layout': '排版规则',
            'semantics.metadata': '仅元数据差异', 'version.orderable': '版本可排序'
        },
        evidenceColumn: '证据详情',
        showEvidence: '查看证据详情',
        showNamingDetails: '查看命名依据',
        reportPath: '字体 {0}',
        reportMetadata: '目录和字体技术',
        comparisonEvidence: '比对依据',
        verificationDetails: '验证结果',
        deferredVerification: '无法验证的项目',
        notApplicableVerification: '不适用的项目',
        verificationHint: '“无法验证”表示当前环境缺少检测能力或尚无法证明结果；“不适用”表示字体未声明该可选特性。两者都不算校验失败，也不获得质量加分。',
        embeddingHint: '嵌入权限读取 OS/2 fsType：只有值为 0 时标为无限制；其它值可能仍允许受限用途。',
        modifiedScoreHint: '“最新修改加分”不是时间戳：扫描范围内修改时间最新的文件得 5 分，其余为 0 分。',
        fileCanReplace: '字体 {0} 可替代字体 {1}',
        yesValue: '是',
        noValue: '否',
        noEvidence: '此记录没有附加证据。',
        collisionFingerprint: '冲突指纹',
        diagnosticLabel: '诊断信息',
        fileNumber: '文件 #{0}',
        planNumber: '计划 #{0} · {1} 项',
        batchNumber: '批次 #{0} · {1} 项',
        differenceTypes: '不同的内容区域',
        pairFace: '字体 {0} · Face #{1}',
        familyLabel: '字体家族',
        styleLabel: '样式',
        weightLabel: '字重',
        widthLabel: '字宽',
        italicLabel: '倾斜',
        postScriptLabel: 'PostScript 名称',
        versionLabel: '版本',
        digestLabel: '严格内容指纹',
        sharedNamesLabel: '双方共用的名称',
        moreFieldDifferences: '另有 {0} 项字段差异，见证据详情',
        handlingAdvice: '处理建议',
        missingDiagnosticHint: '此批次没有保存具体失败原因；重新分析后可查看诊断。',
        unclassifiablePairState: '关系无法分类',
        unclassifiableFileState: '文件无法分类',
        unclassifiableFileGuidance: '当前无法证明此文件的 Unicode 字符覆盖，不能参与自动去重或隔离。文件名可提示款式，但不能证明字形和字符映射关系；请保留原文件并检查 cmap。',
        conflictGuidance: {
            versionConflict: '两个版本的内容不同。请确认字幕实际使用的版本并测试显示效果；确认前保留两份，不按版本号自动删除。',
            sameIdentityContentConflict: '名称和样式相同，但内容不一致。请逐一预览字幕使用到的字符，再决定保留哪份。',
            postScriptNameConflict: '两个不同字体声明了相同的 PostScript 名称，按名称选取可能产生歧义。请核对实际使用路径和显示效果，再手动整理。',
            crossNameConflict: '双方有相同的别名，但字体标识或样式不同。先核对下方共用名称及双方样式；不同字重或斜体通常都应保留。',
            unclassifiable: '缺少完整的深度校验证据；当前不会建议隔离。先查看失败原因，再重新分析或检查原文件。',
            unclassifiableDifferentStyle: '双方的字重或样式不同，通常应保留两份。深度校验失败，暂不能判定内容能否互相替代；请查看失败原因。'
        },
        semanticDifferenceLabels: {
            metadata: '名称与元数据', mapping: '字符映射', outlinesAndMetrics: '字形与度量',
            layout: '排版规则', hinting: '字形微调', colorAndBitmap: '彩色或位图',
            variation: '可变字体', otherFunctional: '其他功能表'
        },
        differingCodePoints: '差异码点',
        replacementDirection: '直接替代方向',
        collectionFaceImpact: '集合影响',
        scoreBreakdown: '评分明细',
        retainedRationale: '保留原因',
        overviewFiles: '已发布文件',
        overviewFaces: '已发布 Face',
        overviewAnalysis: '最新分析批次',
        overviewCandidates: '候选对',
        overviewGroups: '重复组',
        overviewBytes: '可回收字节',
        overviewGeneration: '已发布代次',
        overviewEligible: '可隔离文件数',
        relationshipFilter: '关系',
        unclassifiableFilter: '无法分类状态',
        onlyUnclassifiable: '仅无法分类',
        excludeUnclassifiable: '排除无法分类',
        directoryFilter: '精确目录',
        technologyFilter: '字体技术',
        actionFilter: '动作',
        allValues: '全部',
        applyFilters: '应用筛选',
        unclassifiableState: '无法分类',
        databaseChecked: '健康检查时间',
        databaseSchema: '架构版本',
        databaseJournal: '日志模式',
        databaseSize: '数据库大小',
        databaseRecovery: '已保留损坏数据库',
        notAvailable: '无',
        detailsAction: '详情',
        exportComplete: '分析批次 #{0} 的报告已导出。',
        confirmAllVerifiedFirst: '将准备隔离当前批次的全部已验证重复字体。是否继续查看最终确认？',
        confirmAllVerified: '最终确认：隔离分析批次 {batchId} 中 {itemCount} 个物理文件，共 {bytes} 字节。此操作将由计划任务执行。是否继续？',
        confirmPurge: '确认请求清除所有已到期的字体隔离项？',
        confirmRebuild: '确认请求重建字体派生数据？',
        relationshipLabels: {
            identicalFileHash: '文件哈希相同', strictFaceDuplicate: '字体面严格重复',
            renderingAndLayoutEquivalent: '渲染与排版等效', metadataOnlyDifference: '仅元数据不同',
            coverageSuperset: '字符覆盖为超集', coverageVariant: '字符覆盖不同',
            smallGlyphDifference: '少量字形差异', layoutOnlyDifference: '仅排版差异',
            hintingOnlyDifference: '仅微调差异', versionConflict: '版本冲突',
            sameIdentityContentConflict: '同名内容冲突', postScriptNameConflict: 'PostScript 名称冲突',
            crossNameConflict: '跨名称冲突', independent: '独立字体', unclassifiable: '无法分类'
        },
        actionLabels: { retain: '保留', quarantineCandidate: '可隔离候选', reportOnly: '仅报告', conflict: '冲突' },
        decisionLabels: ['保留', '可隔离候选'],
        verificationLabels: {
            checksums: '校验和', embeddingRights: '嵌入权限', skia: 'Skia 渲染',
            harfbuzz: 'HarfBuzz 排版', freetype: 'FreeType', libass: 'libass',
            layout: '排版', vertical: '竖排', color: '彩色字体', axes: '可变轴'
        },
        verificationStates: { Unknown: '无法验证', Passed: '通过', Failed: '未通过', NotApplicable: '不适用' },
        embeddingStates: { Unknown: 'OS/2 信息不足，无法判断', Passed: '无限制嵌入', Failed: '非无限制嵌入' },
        scoreComponents: { content: '内容', version: '版本', technical: '技术', names: '命名', modified: '最新修改加分' },
        analysisStatuses: ['运行中', '已完成', '已过期', '失败'],
        renameStatuses: ['待处理', '移动中', '临时文件', '已重命名', '回滚中', '已回滚', '目标已占用', '证据已变化', '正在使用', '已跳过', '文件操作失败'],
        batchStatuses: ['运行中', '已完成', '部分完成', '失败', '已取消'],
        quarantineStatuses: ['待隔离', '暂存完成', '已隔离', '已恢复', '已清除', '证据已变化', '替代字体缺失', '目标已占用', '校验失败', '文件操作失败', '已取消', '恢复中', '已复制待移除源文件'],
        batchKinds: ['分析', '隔离', '恢复', '清理', '规范文件名', '回滚文件名', '重建派生数据'],
        renameErrors: {
            'automatic-quarantine-candidate': '自动隔离候选，不参与重命名',
            'missing-identity-or-path-too-long': '缺少可靠名称或目标路径过长',
            'already-normalized': '文件名已经规范',
            'destination-occupied': '目标路径已被占用',
            'protected-or-not-custom': '受保护目录或非自定义字体目录',
            'unclassifiable-or-link': '无法分类或链接文件',
            'in-use': '文件正在使用',
            'source-missing': '源文件不存在',
            'hard-link-or-unknown-link-count': '硬链接或链接数未知',
            'linked-path': '路径包含符号链接',
            'read-only': '目录只读',
            'lease-timeout-or-cancellation': '使用锁超时或操作已取消',
            'source-evidence-changed': '源文件证据已变化'
        },
        phaseLabels: { 'inventory-scan': '字体库存扫描', 'candidate-generation': '候选生成', 'pair-analysis': '字体比对', 'grouping': '重复分组', 'retention': '保留决策', 'publishing': '结果发布' }
    },
    en: {
        subtitleMode: 'Subtitle Processing Mode',
        customFontDirs: 'Custom Font Directories',
        protectedFontDirs: 'Protected Font Directories',
        preferredFontDirs: 'Preferred Font Directories',
        scanFrameRate: 'Scan Frame Rate',
        maxCacheSize: 'Max Cache Size (MB)',
        fontHistoryRetention: 'Analysis reports and inventory generations to retain',
        enableAutoScan: 'Enable Auto Scan Processing',
        enablePrefetch: 'Enable Prefetch Next Episode',
        prefetchTrigger: 'Prefetch Trigger Percent (%)',
        fallbackOnError: 'Fallback to Original on Error',
        saveBtn: 'Save',
        optSubsetting: 'Font Subsetting',
        optConvertSup: 'Convert to SUP/PGS',
        optGenerateMks: 'Generate MKS (Experimental)',
        descSubtitleMode: 'Choose the subtitle processing mode. MKS generation is experimental, targets compatible players such as mpv, must be enabled manually, and is never selected through client detection. The modes are mutually exclusive.',
        descCustomFontDirs: 'E.g., /fonts/my_anime_fonts;/media/extra_fonts. Separate multiple paths with a semicolon (;). (System default font directories are automatically included)',
        descProtectedFontDirs: 'Additional paths that duplicate cleanup and renaming must never modify.',
        descPreferredFontDirs: 'Paths preferred for runtime lookup and duplicate retention.',
        descScanFrameRate: 'Rendering sampling frame rate during ASS to SUP conversion. Higher values result in smoother animations but larger files (Recommended: 24). Video resolution is automatically fetched from metadata.',
        descMaxCacheSize: 'The maximum total volume of processed temporary subtitle cache files. Once the limit is reached, the oldest cache will be automatically cleared (Recommended: 1024).',
        descFontHistoryRetention: 'Keep the latest N analysis reports and their published inventory generations. Set to 0 to retain all. The current inventory and data needed by active operations or plans remain protected.',
        descAutoScan: 'When enabled, new videos added to the media library will automatically have their subtitles pre-processed in the background.',
        descPrefetch: 'When enabled, the plugin will automatically pre-process all ASS subtitles for the next episode in the background when the user\'s playback progress nears the end of the current episode.',
        descPrefetchTrigger: 'Triggers the pre-processing of the next episode when playback progress reaches this percentage (Recommended: 85).',
        descFallback: 'When enabled, if processing fails, the original subtitle will be seamlessly returned to ensure playback is not interrupted.',
        generalSettingsTab: 'General Settings',
        fontManagementTab: 'Font Management',
        fontOverviewTab: 'Overview',
        fontAnalysisTab: 'Analysis Reports',
        fontQuarantineTab: 'Quarantine',
        fontRenameTab: 'Filename Renaming',
        fontDatabaseTab: 'Database Health',
        overviewIntro: 'Published inventory, latest analysis, database health and recent processing times.',
        overviewScanChanges: 'Last Scan Change Reasons',
        overviewAnalysisValidity: 'Analysis Validity',
        analysisCurrent: 'Analysis is current',
        analysisStale: 'Analysis is stale',
        renameIntro: 'Review current source paths, targets and evidence before executing selected pending items.',
        databaseIntro: 'Database state and recent font processing times.',
        analyzeFonts: 'Analyze Fonts',
        exportReport: 'Export Report',
        analysisBatch: 'Analysis Batch',
        quarantineIntro: 'View each font by its current quarantine state. Quarantined files can be selected for restore.',
        quarantineStatusFilter: 'Quarantine status',
        refreshQuarantine: 'Refresh quarantine',
        quarantineDone: 'Quarantined',
        quarantineFailed: 'Failed',
        quarantineWaiting: 'Pending',
        quarantinePath: 'Quarantine path',
        quarantineTargetPath: 'Target quarantine path',
        quarantinePermissionDenied: 'Jellyfin cannot move or remove the source font. Check write access to the source file parent directory for the jellyfin user.',
        quarantineIoFailure: 'File I/O failed',
        renameBatch: 'Rename Plan or Batch',
        renameHistory: 'Rename History',
        renameStatusFilter: 'Item status',
        renamePendingFilter: 'Pending',
        refreshRenameCandidates: 'Refresh rename candidates',
        renameCandidatesPending: 'Waiting to generate rename candidates',
        renameCandidatesRunning: 'Generating rename candidates',
        renameCandidatesReady: 'Rename candidates are ready',
        renameCandidatesStale: 'Rename candidates are stale',
        renameCandidatesFailed: 'Rename candidate generation failed',
        renameCandidatesGenerated: 'Last generated: {0}',
        renameCandidatesNeedAnalysis: 'Complete a font scan and analysis first',
        renameFailedFilter: 'Failed', renameRenamedFilter: 'Renamed', renameRolledbackFilter: 'Rolled back',
        publicationHelp: 'A complete successful scan publishes a generation and analysis only when files, directory policy or evidence version change. Unchanged scans reuse both; failed or cancelled scans publish neither. Renaming publishes no plan batches.',
        staleReasonLabels: { 'files-changed': 'Files changed', 'analysis-incomplete': 'Analysis incomplete', 'generation-superseded': 'Source generation superseded', 'analysis-superseded': 'Analysis superseded', 'directory-policy-changed': 'Directory policy changed', 'evidence-version-changed': 'Evidence version changed', 'initial-scan': 'Initial scan' },
        lastScanChanged: 'Last scan: new generation and analysis published', lastScanUnchanged: 'Last scan: unchanged, generation and analysis reused',

        renameSkippedFilter: 'Skipped',
        analysisIntro: 'Verified whole-file replacement plans are shown once. Conflicts and coverage differences remain separate findings. Expand a card for comparisons and scoring.',
        replacementTitle: 'Verified files ready for quarantine',
        replacementSummary: '{0} retained files · {1} quarantine candidates',
        retainedFilesTitle: 'Retained files',
        candidateFilesTitle: 'Quarantine candidates',
        replacementEvidence: 'Show Face comparisons and scoring',
        selectedFilesStatus: '{0} files selected (including other pages)',
        clearSelection: 'Clear selection',
        selectPlan: 'Select this plan',
        deselectPlan: 'Deselect this plan',
        moreFiles: 'Show {0} more files',
        batchColumn: 'Batch',
        kindColumn: 'Kind',
        selectColumn: 'Select',
        groupColumn: 'Group',
        fileColumn: 'File ID',
        faceColumn: 'Faces',
        decisionColumn: 'Decision',
        scoreColumn: 'Score',
        reasonColumn: 'Reason',
        actionsColumn: 'Actions',
        statusColumn: 'Status',
        expiryColumn: 'Expires',
        errorColumn: 'Error',
        oldPathColumn: 'Original Path',
        newPathColumn: 'Target Path',
        previousPage: 'Previous',
        nextPage: 'Next',
        jumpToPage: 'Go to page',
        pageNumber: 'Page {0}',
        quarantineSelected: 'Quarantine Selected',
        quarantineAllVerified: 'Quarantine all verified',
        restoreSelected: 'Restore Selected Fonts',
        purgeExpired: 'Purge Expired',
        normalizeSelected: 'Rename Selected',
        rollbackSelected: 'Rollback Selected',
        performanceSamples: 'Performance Samples',
        phaseColumn: 'Phase',
        durationColumn: 'Duration',
        itemsColumn: 'Items',
        reusedColumn: 'Reused',
        recordedColumn: 'Recorded',
        rebuildDerivedData: 'Rebuild Derived Data',
        loadingState: 'Loading…',
        emptyState: 'No records to display.',
        errorState: 'Unable to load: {0}',
        selectRequired: 'Select at least one item first.',
        operationQueued: 'Operation request #{0} was persisted with status {1}.',
        operationStatus: 'Operation request #{0}: {1}',
        operationFailed: 'Operation request #{0}: {1}. {2}',
        pageStatus: 'Page {0}, {1} total records',
        groupDetails: 'Group #{0}: constraints {1}; skip reasons {2}; evidence pairs {3}',
        groupHeading: 'Group #{0} · {1} files · {2} Faces',
        groupFilesTitle: 'Files in this group',
        groupPairsTitle: 'Within-group comparisons · {0}',
        groupOtherPairs: 'Other within-group comparisons · {0}',
        groupTechnicalTitle: 'Decision evidence and retention reasons',
        groupConstraintsTitle: 'Decision constraints',
        groupReasonsTitle: 'Retention reasons',
        groupFacesLabel: 'Faces in file',
        groupFaceCount: '{0} Faces',
        groupReclaimable: 'Reclaimable {0}',
        groupPairLabel: 'Face #{0} and Face #{1}',
        groupNoPairs: 'No within-group comparison evidence was saved.',
        reasonQuality: 'Retained by quality score and stable tie-breaks.',
        reasonDirectReplacement: 'Directly replaced by retained file #{0}.',
        reasonProtected: 'Directory is protected; file must be retained.',
        reasonLinked: 'Linked paths prevent cleanup.',
        reasonIncomplete: 'Classification evidence is incomplete; file must be retained.',
        collectionImpactText: 'File #{0} contains {1} Faces; {2} other Faces share that file.',
        constraintLabels: {
            'classification.incomplete': 'Incomplete classification', 'coverage.intersection': 'Overlapping coverage', 'coverage.superset': 'Coverage superset',
            'face.index': 'Collection Face index', 'glyphs.incomplete': 'Incomplete glyph proof', 'identity.independent': 'Independent identity',
            'identity.same': 'Same identity', 'name.postscript': 'PostScript name', 'semantics.all': 'Matching semantic content',
            'semantics.conflict': 'Conflicting semantic content', 'semantics.hinting': 'Hinting', 'semantics.layout': 'Layout',
            'semantics.metadata': 'Metadata difference', 'version.orderable': 'Orderable version'
        },
        evidenceColumn: 'Evidence',
        showEvidence: 'View evidence',
        showNamingDetails: 'View naming evidence',
        reportPath: 'Font {0}',
        reportMetadata: 'Directories and technology',
        comparisonEvidence: 'Comparison evidence',
        verificationDetails: 'Verification',
        deferredVerification: 'Checks without proof',
        notApplicableVerification: 'Not applicable',
        verificationHint: 'Unavailable proof means the environment lacks a verifier or the result could not be established. Not applicable means the font does not declare that optional feature. Neither is a failure or earns quality points.',
        embeddingHint: 'Embedding rights come from OS/2 fsType. Only 0 is marked unrestricted; other values may still permit limited uses.',
        modifiedScoreHint: 'Newest modification bonus is a score, not a timestamp: the newest file in the scan receives 5 points; others receive 0.',
        fileCanReplace: 'Font {0} can replace font {1}',
        yesValue: 'Yes',
        noValue: 'No',
        noEvidence: 'No additional evidence for this record.',
        collisionFingerprint: 'Collision fingerprint',
        diagnosticLabel: 'Diagnostic',
        fileNumber: 'File #{0}',
        planNumber: 'Plan #{0} · {1} items',
        batchNumber: 'Batch #{0} · {1} items',
        differenceTypes: 'Different content areas',
        pairFace: 'Font {0} · Face #{1}',
        familyLabel: 'Family',
        styleLabel: 'Style',
        weightLabel: 'Weight',
        widthLabel: 'Width',
        italicLabel: 'Italic',
        postScriptLabel: 'PostScript name',
        versionLabel: 'Version',
        digestLabel: 'Strict content digest',
        sharedNamesLabel: 'Names shared by both',
        moreFieldDifferences: '{0} more field differences in evidence details',
        handlingAdvice: 'Suggested handling',
        missingDiagnosticHint: 'This batch did not save a specific failure reason; rerun analysis for diagnostics.',
        unclassifiablePairState: 'Relationship unclassifiable',
        unclassifiableFileState: 'File unclassifiable',
        unclassifiableFileGuidance: 'This file has no proven Unicode coverage and cannot enter automatic deduplication or quarantine. Its filename may suggest a style but cannot establish glyph or mapping equivalence; keep the source file and inspect cmap.',
        conflictGuidance: {
            versionConflict: 'The versions contain different content. Check which version subtitles use and test rendering; keep both until verified.',
            sameIdentityContentConflict: 'The name and style match but content differs. Preview characters used by subtitles before choosing either file.',
            postScriptNameConflict: 'Different fonts declare the same PostScript name, making name-based selection ambiguous. Check the actual path and rendering before manual cleanup.',
            crossNameConflict: 'These fonts share an alias but have different identities or styles. Compare the shared name and each style below; distinct weights or italics usually belong together.',
            unclassifiable: 'Deep verification is incomplete, so quarantine is not suggested. Read the failure reason, then reanalyze or inspect the source file.',
            unclassifiableDifferentStyle: 'The weights or styles differ, so both usually belong together. Deep verification failed; review the reason before judging whether either can replace the other.'
        },
        semanticDifferenceLabels: {
            metadata: 'Names and metadata', mapping: 'Character mapping', outlinesAndMetrics: 'Outlines and metrics',
            layout: 'Layout rules', hinting: 'Hinting', colorAndBitmap: 'Color or bitmap',
            variation: 'Variable axes', otherFunctional: 'Other functional tables'
        },
        differingCodePoints: 'Differing code points',
        replacementDirection: 'Direct replacement',
        collectionFaceImpact: 'Collection impact',
        scoreBreakdown: 'Score breakdown',
        retainedRationale: 'Retained rationale',
        overviewFiles: 'Published Files',
        overviewFaces: 'Published Faces',
        overviewAnalysis: 'Latest Analysis Batch',
        overviewCandidates: 'Candidate Pairs',
        overviewGroups: 'Duplicate Groups',
        overviewBytes: 'Reclaimable Bytes',
        overviewGeneration: 'Published Generation',
        overviewEligible: 'Eligible Quarantine Files',
        relationshipFilter: 'Relationship',
        unclassifiableFilter: 'Unclassifiable State',
        onlyUnclassifiable: 'Only unclassifiable',
        excludeUnclassifiable: 'Exclude unclassifiable',
        directoryFilter: 'Exact Directory',
        technologyFilter: 'Technology',
        actionFilter: 'Action',
        allValues: 'All',
        applyFilters: 'Apply Filters',
        unclassifiableState: 'Unclassifiable',
        databaseChecked: 'Health Checked',
        databaseSchema: 'Schema Version',
        databaseJournal: 'Journal Mode',
        databaseSize: 'Database Size',
        databaseRecovery: 'Preserved Corrupt Database',
        notAvailable: 'None',
        detailsAction: 'Details',
        exportComplete: 'The report for analysis batch #{0} was exported.',
        confirmAllVerifiedFirst: 'This will prepare every verified duplicate in the current batch for quarantine. Continue to final confirmation?',
        confirmAllVerified: 'Final confirmation: quarantine {itemCount} physical files from analysis batch {batchId}, totalling {bytes} bytes. The scheduled task will execute this operation. Continue?',
        confirmPurge: 'Request purge of all expired font quarantine items?',
        confirmRebuild: 'Request a rebuild of font derived data?',
        relationshipLabels: {
            identicalFileHash: 'Identical file hash', strictFaceDuplicate: 'Strict face duplicate',
            renderingAndLayoutEquivalent: 'Rendering and layout equivalent', metadataOnlyDifference: 'Metadata-only difference',
            coverageSuperset: 'Coverage superset', coverageVariant: 'Coverage variant',
            smallGlyphDifference: 'Small glyph difference', layoutOnlyDifference: 'Layout-only difference',
            hintingOnlyDifference: 'Hinting-only difference', versionConflict: 'Version conflict',
            sameIdentityContentConflict: 'Same-identity content conflict', postScriptNameConflict: 'PostScript-name conflict',
            crossNameConflict: 'Cross-name conflict', independent: 'Independent fonts', unclassifiable: 'Unclassifiable'
        },
        actionLabels: { retain: 'Retain', quarantineCandidate: 'Quarantine candidate', reportOnly: 'Report only', conflict: 'Conflict' },
        decisionLabels: ['Retained', 'Quarantine candidate'],
        verificationLabels: {
            checksums: 'Checksums', embeddingRights: 'Embedding rights', skia: 'Skia rendering',
            harfbuzz: 'HarfBuzz shaping', freetype: 'FreeType', libass: 'libass',
            layout: 'Layout', vertical: 'Vertical', color: 'Color', axes: 'Axes'
        },
        verificationStates: { Unknown: 'Cannot verify', Passed: 'Passed', Failed: 'Failed', NotApplicable: 'Not applicable' },
        embeddingStates: { Unknown: 'Cannot determine without OS/2 data', Passed: 'Unrestricted embedding', Failed: 'Not unrestricted embedding' },
        scoreComponents: { content: 'Content', version: 'Version', technical: 'Technical', names: 'Names', modified: 'Newest modification bonus' },
        analysisStatuses: ['Running', 'Complete', 'Stale', 'Failed'],
        renameStatuses: ['Pending', 'Moving', 'Temporary', 'Renamed', 'Rolling back', 'Rolled back', 'Destination occupied', 'Evidence drift', 'In use', 'Skipped', 'File I/O failed'],
        batchStatuses: ['Running', 'Complete', 'Partial', 'Failed', 'Cancelled'],
        quarantineStatuses: ['Pending quarantine', 'Staged', 'Quarantined', 'Restored', 'Purged', 'Evidence changed', 'Replacement missing', 'Destination occupied', 'Verification failed', 'File I/O failed', 'Cancelled', 'Restoring', 'Copy promoted; source removal pending'],
        batchKinds: ['Analyze', 'Quarantine', 'Restore', 'Purge', 'Normalize names', 'Rollback names', 'Rebuild derived data'],
        renameErrors: {
            'automatic-quarantine-candidate': 'Automatic quarantine candidate',
            'missing-identity-or-path-too-long': 'Missing reliable name or target path too long',
            'already-normalized': 'Filename already normalized',
            'destination-occupied': 'Destination occupied',
            'protected-or-not-custom': 'Protected or non-custom directory',
            'unclassifiable-or-link': 'Unclassifiable or linked file',
            'in-use': 'File in use',
            'source-missing': 'Source file missing',
            'hard-link-or-unknown-link-count': 'Hard link or unknown link count',
            'linked-path': 'Path contains a symbolic link',
            'read-only': 'Read-only directory',
            'lease-timeout-or-cancellation': 'File lease timed out or operation cancelled',
            'source-evidence-changed': 'Source evidence changed'
        },
        phaseLabels: { 'inventory-scan': 'Inventory scan', 'candidate-generation': 'Candidate generation', 'pair-analysis': 'Pair analysis', 'grouping': 'Duplicate grouping', 'retention': 'Retention selection', 'publishing': 'Publish results' }
    }
};

Object.assign(i18nStrings.zh, {
    "fileNameRule": "文件名模板",
    "folderRule": "文件夹模板",
    "removeEmptyDirs": "递归移除空目录",
    "ruleSettingsIntro": "保留元数据原有大小写，扩展名自动转为小写追加。文件夹模板从所属自定义源根展开；留空保持原目录。保存设置不会移动文件。",
    "removeEmptyHelp": "仅逐级移除成功移动后留下的空祖先目录；源根、含隐藏文件及受保护目录始终保留。",
    "namingHelp": "模板占位符与示例",
    "copyDefaultRule": "复制默认规则",
    "resetDefaultRule": "重置文件名规则",
    "ruleSaved": "规则已保存，请在文件名重命名页面刷新预览。",
    "refreshRenamePreview": "刷新重命名预览",
    "renameCurrent": "预览有效，可选择执行",
    "renameStale": "预览已过期，请刷新后执行",
    "renameRules": "预览使用的规则",
    "originalDirectory": "原目录",
    "targetDirectory": "目标目录",
    "originalFilename": "原文件名",
    "targetFilename": "目标文件名",
    "sourceRoot": "所属源根",
    "namingField": "字段",
    "namingValue": "实际值",
    "namingProvenance": "来源",
    "namingEmptyValue": "空字符串",
    "directoryResults": "目录创建与空目录清理",
    "directorySummary": "已移除 {0} · 保留 {1} · 警告 {2}",
    "directoryActions": [
        "创建目录",
        "移除空目录"
    ],
    "directoryStatuses": [
        "待处理",
        "完成",
        "保留",
        "警告"
    ],
    "fieldDescriptions": {
        "family": "程序选择的家族；优先完整排印名称对，再选完整传统对；集合家族去重排序并以 + 连接",
        "style": "与家族使用同一来源的样式；集合要求全部 Face 一致",
        "fullName": "Name ID 4 完整名称，无其它字段回退；集合要求一致",
        "postscript": "Name ID 6 PostScript 名，无其它字段回退；集合要求一致",
        "manufacturer": "Name ID 8 制造商；集合要求全部 Face 一致",
        "version": "可靠版本文本，如 1.004；不含 v 和分隔符；集合版本须一致，保留精度",
        "legacyFamily": "Name ID 1 传统家族，无排印记录回退",
        "legacyStyle": "Name ID 2 传统样式",
        "typographicFamily": "Name ID 16 排印家族，无传统记录回退",
        "typographicStyle": "Name ID 17 排印样式",
        "styleSuffix": "完整样式后缀 -样式；样式不可用时整体为空",
        "versionSuffix": "完整版本后缀 -v版本；版本不可用时整体为空",
        "collectionSuffix": "程序生成 -Collection 或 -MixedCollection；单字体有意为空",
        "originalName": "当前文件名主干，不含扩展名；仅显式引用时使用",
        "faceCount": "实际库存 Face 数量，十进制文本",
        "fingerprint": "规范 Face 内容或集合汇总指纹前 8 位小写十六进制；与文件 SHA-256 不同"
    },
    "namingSyntax": "模板由普通文本与 {field} 组成，字段名大小写敏感。可用 {upper(family)}、{lower(style)} 做与系统语言无关的大小写转换；不支持嵌套。只有文件夹模板的字面 / 或 \\ 建立目录层级。",
    "namingExamples": "完整名称可用 {fullName}{versionSuffix}；按制造商/家族整理可用文件夹 {manufacturer}/{family}。扩展名自动追加，无需写入模板。",
    "namingFieldUsage": "字段使用与缺失值",
    "namingFieldUsageHelp": "必要字段不可用时跳过；样式、制造商、版本等可选字段为空，后缀连同分隔符整体省略。仅由可选字段构成的空目录层会省略；非空模板全部层省略时目标是源根。程序不会编造原名回退或样式。",
    "namingCollisionRule": "默认版本与冲突后缀规则",
    "namingCollisionHelp": "默认模板让所有有可靠共同版本的文件输出 -v版本；删除 versionSuffix 可主动不输出版本。碰撞时只追加规范短指纹，仍冲突则跳过。已经占用展开目标的文件优先保留；已规范化跳过并不代表没有版本。",
    "evidenceSources": {
        "inventory-face": "库存 Face 记录",
        "inventory-file": "当前库存文件",
        "collection-summary": "集合汇总",
        "program-suffix": "程序生成后缀",
        "canonical-fingerprint": "规范内容指纹"
    },
    "ruleErrorPosition": "规则位置 {0}：{1}",
    "namingErrors": {
        "unknown-field": "未知字段",
        "unknown-function": "未知格式化操作",
        "unsupported-syntax": "不支持的模板语法",
        "invalid-format-argument": "格式化操作只接受一个文本字段",
        "nested-template-brace": "占位符内不能嵌套花括号",
        "empty-template": "文件名模板不能为空",
        "template-too-long": "模板超过 16,384 字符",
        "too-many-template-parts": "模板超过 512 个片段",
        "invalid-path-separator": "路径分隔符位置无效",
        "invalid-path-literal": "路径字面量无效",
        "empty-path-component": "目录层为空",
        "path-traversal": "禁止 . 或 .. 路径组件",
        "unclosed-template-expression": "占位符花括号未闭合",
        "unsupported-rule-version": "不支持的规则版本，请重置或编辑模板后重新生成预览",
        "missing-field": "缺少字段",
        "missing-family": "缺少同源家族",
        "blank-field": "字段仅含空白",
        "sanitized-empty": "字符净化后为空",
        "inconsistent-face-field": "各 Face 的字段值不一致",
        "unreliable-version": "版本不可靠",
        "no-faces": "没有可用的字体面",
        "invalid-fingerprint": "内容指纹不完整",
        "invalid-unicode": "无效 Unicode",
        "empty-or-unsafe-component": "结果为空或不是安全路径组件",
        "evaluation-limit-exceeded": "模板展开超过资源限制",
        "path-too-long-or-unsafe-name": "路径预算不足或截短后名称不安全",
        "fingerprint-collision": "短指纹仍冲突，无法消歧",
        "protected-or-not-custom": "受保护目录或未归属自定义源根",
        "unclassifiable-or-link": "字体不可分类或存在链接",
        "different-filesystem": "目标位于不同文件系统",
        "target-root-changed": "目标属于另一项自定义源根",
        "outside-source-root": "目标超出所属源根"
    },
    "namingIntentionalEmpty": "有意为空",
    "namingSelections": "名称选择与集合汇总",
    "selectionKinds": {
        "typographic-pair": "完整排印名称对",
        "legacy-pair": "完整传统名称对",
        "family-only": "仅家族（缺少样式）",
        "collection-summary": "集合汇总"
    },
    "missingPolicies": {
        "skip": "不可用时：引用则跳过",
        "empty": "不可用时：空字符串",
        "collection-family-required": "单字体为空；集合缺少家族时引用则跳过"
    },
    "namingFormatting": "文本格式化与路径",
    "namingDefaultLabel": "默认文件名模板",
    "namingVersionAvailable": "可靠版本",
    "renameSkipReason": "跳过原因",
    "renameUnclassifiableReason": "字体检查未通过",
    "namingFingerprintUnused": "未用于消歧"
});
Object.assign(i18nStrings.en, {
    "fileNameRule": "Filename template",
    "folderRule": "Folder template",
    "removeEmptyDirs": "Remove empty ancestor directories",
    "ruleSettingsIntro": "Metadata case is preserved. The source extension is appended in lowercase. Folder templates start at the owning custom root; blank keeps the current directory. Saving settings does not move files.",
    "removeEmptyHelp": "Remove empty ancestors only after successful moves. Source roots, directories with hidden files, and protected directories remain.",
    "namingHelp": "Template placeholders and examples",
    "copyDefaultRule": "Copy default rule",
    "resetDefaultRule": "Reset filename rule",
    "ruleSaved": "Rules saved. Refresh the filename preview before execution.",
    "refreshRenamePreview": "Refresh rename preview",
    "renameCurrent": "Current preview; execution available",
    "renameStale": "Preview is stale. Refresh before execution.",
    "renameRules": "Preview rules",
    "originalDirectory": "Original directory",
    "targetDirectory": "Target directory",
    "originalFilename": "Original filename",
    "targetFilename": "Target filename",
    "sourceRoot": "Owning source root",
    "namingField": "Field",
    "namingValue": "Used value",
    "namingProvenance": "Source",
    "namingEmptyValue": "Empty string",
    "directoryResults": "Directory creation and empty-directory cleanup",
    "directorySummary": "Removed {0} · Retained {1} · Warnings {2}",
    "directoryActions": [
        "Create directory",
        "Remove empty directory"
    ],
    "directoryStatuses": [
        "Pending",
        "Completed",
        "Retained",
        "Warning"
    ],
    "fieldDescriptions": {
        "family": "Selected family: complete typographic pair, then complete legacy pair; collection families are sorted, deduplicated and joined with +",
        "style": "Style from the same source as the family; every collection Face must agree",
        "fullName": "Name ID 4 full name, without fallback; collection Faces must agree",
        "postscript": "Name ID 6 PostScript name, without fallback; collection Faces must agree",
        "manufacturer": "Name ID 8 manufacturer; all collection Faces must agree",
        "version": "Reliable version text, e.g. 1.004; excludes v and separators; all Faces must agree, preserving precision",
        "legacyFamily": "Name ID 1 legacy family, without typographic fallback",
        "legacyStyle": "Name ID 2 legacy style",
        "typographicFamily": "Name ID 16 typographic family, without legacy fallback",
        "typographicStyle": "Name ID 17 typographic style",
        "styleSuffix": "Complete -style suffix; empty when style is unavailable",
        "versionSuffix": "Complete -vVersion suffix; empty when version is unavailable",
        "collectionSuffix": "Automatic -Collection or -MixedCollection; intentionally empty for single fonts",
        "originalName": "Current filename stem, without extension; used only when explicitly referenced",
        "faceCount": "Actual inventory Face count as decimal text",
        "fingerprint": "First eight lowercase canonical Face or collection fingerprint digits; distinct from file SHA-256"
    },
    "namingSyntax": "Combine literal text with {field}; field names are case-sensitive. Use {upper(family)} or {lower(style)} for invariant case formatting; nesting is unsupported. Only literal / or \\ in a folder template creates directory levels.",
    "namingExamples": "Use {fullName}{versionSuffix} for a full name, or {manufacturer}/{family} for folders. The extension is automatic and does not belong in the template.",
    "namingFieldUsage": "Fields and missing values",
    "namingFieldUsageHelp": "Unavailable required fields cause a skip. Optional style, manufacturer and version fields are empty; suffixes omit their separators too. Empty folder layers made entirely of optional fields are omitted. If every configured layer is omitted, the target is the source root. Names and styles are never invented.",
    "namingCollisionRule": "Default version and collision suffix rules",
    "namingCollisionHelp": "The default includes -vVersion for every reliable shared version. Remove versionSuffix to omit it. Collisions add only a short canonical fingerprint; unresolved collisions are skipped. A file already owning the expanded target is retained first. An already-normalized skip does not mean the font has no version.",
    "evidenceSources": {
        "inventory-face": "Saved Face record",
        "inventory-file": "Current inventory file",
        "collection-summary": "Collection summary",
        "program-suffix": "Automatic suffix",
        "canonical-fingerprint": "Canonical content fingerprint"
    },
    "ruleErrorPosition": "Rule character {0}: {1}",
    "namingErrors": {
        "unknown-field": "Unknown field",
        "unknown-function": "Unknown formatting operation",
        "unsupported-syntax": "Unsupported template syntax",
        "invalid-format-argument": "Formatting accepts one text field",
        "nested-template-brace": "Nested placeholder brace",
        "empty-template": "Filename template is empty",
        "template-too-long": "Template exceeds 16,384 characters",
        "too-many-template-parts": "Template exceeds 512 parts",
        "invalid-path-separator": "Invalid path separator",
        "invalid-path-literal": "Invalid path literal",
        "empty-path-component": "Empty directory layer",
        "path-traversal": "Path traversal",
        "unclosed-template-expression": "Unclosed placeholder",
        "unsupported-rule-version": "Unsupported rule version; reset or edit the template and refresh the preview",
        "missing-field": "Missing field",
        "missing-family": "Missing matching family",
        "blank-field": "Blank field",
        "sanitized-empty": "Empty after sanitizing",
        "inconsistent-face-field": "Inconsistent face field",
        "unreliable-version": "Unreliable version",
        "no-faces": "No usable Faces",
        "invalid-fingerprint": "Incomplete content fingerprint",
        "invalid-unicode": "Invalid Unicode",
        "empty-or-unsafe-component": "Empty or unsafe component",
        "evaluation-limit-exceeded": "Template expansion exceeds resource limits",
        "path-too-long-or-unsafe-name": "Path budget exceeded or unsafe truncated name",
        "fingerprint-collision": "Short fingerprint collision cannot be resolved",
        "protected-or-not-custom": "Protected or not custom",
        "unclassifiable-or-link": "Unclassifiable or link",
        "different-filesystem": "Different filesystem",
        "target-root-changed": "Target root changed",
        "outside-source-root": "Outside source root"
    },
    "namingIntentionalEmpty": "Intentionally empty",
    "namingSelections": "Name selection and collection summary",
    "selectionKinds": {
        "typographic-pair": "Complete typographic pair",
        "legacy-pair": "Complete legacy pair",
        "family-only": "Family only (missing style)",
        "collection-summary": "Collection summary"
    },
    "missingPolicies": {
        "skip": "Unavailable: skip when referenced",
        "empty": "Unavailable: empty string",
        "collection-family-required": "Empty for single fonts; missing collection family blocks references"
    },
    "namingFormatting": "Text formatting and paths",
    "namingDefaultLabel": "Default filename template",
    "namingVersionAvailable": "Reliable version",
    "renameSkipReason": "Skip reason",
    "renameUnclassifiableReason": "Font inspection failed",
    "namingFingerprintUnused": "Not used for disambiguation"
});

var fontManagementState = {
    locale: 'en',
    activeView: 'overview',
    pageSize: 50,
    analysisBatchId: null,
    analysisBatch: null,
    reportPage: 1,
    reportTotal: 0,
    reportLoadId: 0,
    selectedFileIds: new Set(),
    quarantinePage: 1,
    quarantineTotal: 0,
    quarantineStatus: 'quarantined',
    quarantineLoadId: 0,
    quarantineSelections: new Map(),
    renamePage: 1,
    renameTotal: 0,
    renameItemPage: 1,
    renameItemTotal: 0,
    renameItems: [],
    renameFileIds: new Set(),
    renameContext: null,
    renameStatus: "pending",
    renameRows: [],
    renameCurrent: false,
    renameLoadId: 0,
    renameReadAbort: null,
    renamePollTimer: null,
    renameStalePollUntil: 0,
    renameCandidateState: null,
    renameCanRefresh: true,
    renameRefreshKey: null,
    renameRefreshInFlight: false,
    pageHidden: false,
    namingDefault: null,
    namingFields: [],
    directoryLoadId: null,
    pollGeneration: 0
};

function currentStrings() {
    return i18nStrings[fontManagementState.locale] || i18nStrings.en;
}

function formatText(template) {
    var args = Array.prototype.slice.call(arguments, 1);
    return String(template || '').replace(/\{(\d+)\}/g, function (match, index) {
        return args[Number(index)] === undefined ? match : String(args[Number(index)]);
    });
}

function formatNamedText(template, values) {
    return String(template || '').replace(/\{([a-zA-Z]+)\}/g, function (match, name) {
        return values[name] === undefined ? match : String(values[name]);
    });
}

function clearElement(element) {
    while (element && element.firstChild) {
        element.removeChild(element.firstChild);
    }
}

function setElementText(elementOrSelector, value) {
    var element = typeof elementOrSelector === 'string' ? document.querySelector(elementOrSelector) : elementOrSelector;
    if (element) {
        element.textContent = value === null || value === undefined ? '' : String(value);
    }
}

function setViewState(selector, kind, message) {
    var element = document.querySelector(selector);
    if (!element) return;
    element.className = 'fontManagementState fontManagementState-' + kind;
    element.textContent = message || '';
}

function createTextCell(row, value) {
    var cell = document.createElement('td');
    cell.textContent = value === null || value === undefined ? '' : String(value);
    row.appendChild(cell);
    return cell;
}

function appendDefinition(list, label, value, wide) {
    var card = document.createElement('div');
    card.className = 'fontMetricCard' + (wide ? ' fontMetricCard--wide' : '');
    var term = document.createElement('dt');
    var description = document.createElement('dd');
    term.textContent = label;
    description.textContent = value === null || value === undefined ? '' : String(value);
    card.appendChild(term);
    card.appendChild(description);
    list.appendChild(card);
    return description;
}

function appendDetail(parent, label, value) {
    var item = document.createElement('div');
    item.className = 'fontDetailItem';
    var title = document.createElement('span');
    title.className = 'fontDetailLabel';
    title.textContent = label;
    var body = document.createElement('span');
    body.className = 'fontDetailValue';
    body.textContent = value === null || value === undefined || value === '' ? currentStrings().notAvailable : String(value);
    item.appendChild(title);
    item.appendChild(body);
    parent.appendChild(item);
}

function appendPath(parent, label, path) {
    var block = document.createElement('div');
    block.className = 'fontPathBlock';
    var title = document.createElement('span');
    title.className = 'fontPathLabel';
    title.textContent = label;
    var name = document.createElement('span');
    name.className = 'fontPathName';
    name.textContent = String(path || '').split(/[\\/]/).pop() || currentStrings().notAvailable;
    var full = document.createElement('span');
    full.className = 'fontPathFull';
    full.textContent = path || currentStrings().notAvailable;
    block.appendChild(title);
    block.appendChild(name);
    block.appendChild(full);
    parent.appendChild(block);
}

function appendBadge(parent, label, caution) {
    var badge = document.createElement('span');
    badge.className = 'fontBadge' + (caution ? ' fontBadge--caution' : '');
    badge.textContent = label;
    parent.appendChild(badge);
}

function localizedValue(value, labels, numericNames) {
    var raw = typeof value === 'number' ? (numericNames && numericNames[value]) : value;
    if (raw === null || raw === undefined || raw === '') return currentStrings().notAvailable;
    var key = String(raw);
    key = key.charAt(0).toLowerCase() + key.slice(1);
    return labels[key] || String(raw);
}

function relationshipName(value) {
    var names = ['', 'IdenticalFileHash', 'StrictFaceDuplicate', 'RenderingAndLayoutEquivalent', 'MetadataOnlyDifference', 'CoverageSuperset', 'CoverageVariant', 'SmallGlyphDifference', 'LayoutOnlyDifference', 'HintingOnlyDifference', 'VersionConflict', 'SameIdentityContentConflict', 'PostScriptNameConflict', 'CrossNameConflict', 'Independent'];
    return localizedValue(value, currentStrings().relationshipLabels, names);
}

function actionName(value) {
    return localizedValue(value, currentStrings().actionLabels, ['Retain', 'QuarantineCandidate', 'ReportOnly', 'Conflict']);
}

function reportKindKey(value) {
    var names = ['', 'IdenticalFileHash', 'StrictFaceDuplicate', 'RenderingAndLayoutEquivalent', 'MetadataOnlyDifference', 'CoverageSuperset', 'CoverageVariant', 'SmallGlyphDifference', 'LayoutOnlyDifference', 'HintingOnlyDifference', 'VersionConflict', 'SameIdentityContentConflict', 'PostScriptNameConflict', 'CrossNameConflict', 'Independent'];
    var raw = typeof value === 'number' ? names[value] : value;
    return raw ? String(raw).charAt(0).toLowerCase() + String(raw).slice(1) : 'unclassifiable';
}

function semanticDifferenceName(value) {
    return currentStrings().semanticDifferenceLabels[reportKindKey(value)] || relationshipName(value);
}

function faceFieldDifferences(left, right) {
    if (!left || !right) return [];
    var strings = currentStrings();
    return [
        [strings.familyLabel, left.family, right.family],
        [strings.styleLabel, left.style, right.style],
        [strings.weightLabel, left.weight, right.weight],
        [strings.widthLabel, left.width, right.width],
        [strings.italicLabel, left.italic, right.italic],
        [strings.postScriptLabel, left.postScriptName, right.postScriptName]
    ].filter(function (field) { return field[1] !== field[2]; }).map(function (field) {
        return [field[0],
            (typeof field[1] === 'boolean' ? (field[1] ? strings.yesValue : strings.noValue) : field[1] ?? strings.notAvailable)
            + ' ↔ ' +
            (typeof field[2] === 'boolean' ? (field[2] ? strings.yesValue : strings.noValue) : field[2] ?? strings.notAvailable)];
    });
}

function summarizeVersion(value) {
    var version = String(value || currentStrings().notAvailable).split(';')[0].trim();
    return version.length > 48 ? version.slice(0, 47) + '…' : version;
}

function formatDiagnostic(value) {
    if (!value || fontManagementState.locale !== 'zh') return value;
    var mappingMismatch = /^U\+([0-9A-F]+): Skia glyph (\d+) differs from OpenType cmap glyph (\d+)\.$/.exec(value);
    if (mappingMismatch)
        return '字符 U+' + mappingMismatch[1] + '：Skia 字形编号 ' + mappingMismatch[2] + ' 与 OpenType cmap 的 ' + mappingMismatch[3] + ' 不一致。';
    var variationMismatch = /^U\+([0-9A-F]+) variation selector U\+([0-9A-F]+): glyph (\d+) is outside the Skia glyph range\.$/.exec(value);
    if (variationMismatch)
        return '字符 U+' + variationMismatch[1] + ' 的变体选择符 U+' + variationMismatch[2] + '：字形编号 ' + variationMismatch[3] + ' 超出 Skia 字形范围。';
    var outlineError = /^TrueType outline parsing failed: Glyph (\d+): Nonzero unparsed glyph bytes\.$/.exec(value);
    if (outlineError)
        return 'TrueType 轮廓解析失败：字形 ' + outlineError[1] + ' 含有未解析的非零字节。';
    var unsupportedCmap = /^Unicode cmap format (\d+) is unsupported\.$/.exec(value);
    if (unsupportedCmap)
        return '暂不支持 Unicode cmap 格式 ' + unsupportedCmap[1] + '。';
    return ({
        'Native glyph mapping or nonempty HarfBuzz subset verification failed; see individual quality outcomes.': '原生字形映射或 HarfBuzz 子集化校验失败；请查看单项质量结果。',
        'HarfBuzz subset verification failed.': 'HarfBuzz 子集化校验失败。',
        'Skia could not open this Face.': 'Skia 无法加载该字体面。',
        'OpenType cmap mappings are incomplete or unsupported.': 'OpenType cmap 字符映射不完整或格式暂不支持。',
        'Complete semantic parsing could not be verified.': '语义解析未完成，无法安全比较。',
        'The published strict Face fingerprint differs from the current font content.': '当前字体内容与库存发布时的严格指纹不一致。',
        'Physical file evidence changed after inventory publication.': '字体文件在库存发布后发生了变化。',
        'No valid encoded Unicode coverage could be proven from cmap.': '未能从 cmap 证明有效的 Unicode 字符覆盖。',
        'Unicode cmap mappings are invalid or inconsistent across subtables.': 'Unicode cmap 映射无效，或各子表之间互相矛盾。',
        'Only legacy cmap encoding records are present; no Unicode mapping can be proven.': '只有旧式 cmap 编码记录，无法证明 Unicode 字符映射。',
        'Unicode cmap has no valid nonzero glyph mappings.': 'Unicode cmap 中没有有效的非零字形映射。',
        'A Unicode cmap record points outside the table.': 'Unicode cmap 记录指向表格范围之外。',
        'The cmap format 4 glyph array range lies outside its OpenType table.': 'cmap 格式 4 的字形引用超出子表声明长度。',
        'The cmap format 4 glyph array reference lies outside the declared subtable length.': 'cmap 格式 4 的字形引用超出子表声明长度。',
        'The cmap format 4 arrays range lies outside its OpenType table.': 'cmap 格式 4 的数据数组超出 OpenType 表格范围。'
    })[value] || value;
}

function renderPairSummary(card, item) {
    var pair = item.pairEvidence;
    if (!pair || (!item.isUnclassifiable && actionName(item.action) !== currentStrings().actionLabels.conflict)) return;
    var strings = currentStrings();
    var summary = document.createElement('div');
    summary.className = 'fontDetailGrid fontPairSummary';
    [pair.leftFace, pair.rightFace].forEach(function (face, index) {
        if (!face) return;
        appendDetail(summary, formatText(strings.pairFace, index + 1, face.faceId),
            [face.family, face.style, face.weight ? strings.weightLabel + ' ' + face.weight : '', face.italic ? strings.italicLabel : ''].filter(Boolean).join(' · '));
    });
    if (pair.sharedNames && pair.sharedNames.length)
        appendDetail(summary, strings.sharedNamesLabel, pair.sharedNames.slice(0, 3).join(' · ') + (pair.sharedNames.length > 3 ? ' +' + (pair.sharedNames.length - 3) : ''));
    var fieldDifferences = faceFieldDifferences(pair.leftFace, pair.rightFace);
    fieldDifferences.slice(0, 4).forEach(function (field) { appendDetail(summary, field[0], field[1]); });
    if (fieldDifferences.length > 4)
        appendDetail(summary, strings.differenceTypes, formatText(strings.moreFieldDifferences, fieldDifferences.length - 4));
    if (pair.leftFace && pair.rightFace && pair.leftFace.version !== pair.rightFace.version)
        appendDetail(summary, strings.versionLabel,
            summarizeVersion(pair.leftFace.version) + ' ↔ ' + summarizeVersion(pair.rightFace.version));
    if (pair.leftFace && pair.rightFace && pair.leftFace.strictFingerprint !== pair.rightFace.strictFingerprint)
        appendDetail(summary, strings.digestLabel, pair.leftFace.strictFingerprint.slice(0, 16) + '… ↔ ' + pair.rightFace.strictFingerprint.slice(0, 16) + '…');
    var contentAreas = (pair.differenceTypes || []).filter(function (value) { return !!strings.semanticDifferenceLabels[reportKindKey(value)]; });
    if (contentAreas.length) appendDetail(summary, strings.differenceTypes, contentAreas.map(semanticDifferenceName).join(' · '));
    var diagnostics = [pair.leftDiagnostic, pair.rightDiagnostic].filter(Boolean).map(formatDiagnostic).filter(function (value, index, values) { return values.indexOf(value) === index; });
    if (diagnostics.length) appendDetail(summary, strings.diagnosticLabel, diagnostics.join(' · '));
    if (item.isUnclassifiable && !diagnostics.length) appendDetail(summary, strings.diagnosticLabel, strings.missingDiagnosticHint);
    card.appendChild(summary);
    var distinctStyle = pair.leftFace && pair.rightFace && (pair.leftFace.style !== pair.rightFace.style || pair.leftFace.weight !== pair.rightFace.weight || pair.leftFace.italic !== pair.rightFace.italic);
    var guidance = strings.conflictGuidance[item.isUnclassifiable ? (distinctStyle ? 'unclassifiableDifferentStyle' : 'unclassifiable') : reportKindKey(item.relationshipKind)];
    if (guidance) {
        var note = document.createElement('p');
        note.className = 'fontPairGuidance';
        note.textContent = strings.handlingAdvice + '：' + guidance;
        card.appendChild(note);
    }
}

function phaseName(value) {
    return currentStrings().phaseLabels[value] || value || currentStrings().notAvailable;
}

function formatNumber(value) {
    return Number(value || 0).toLocaleString(fontManagementState.locale === 'zh' ? 'zh-CN' : 'en-US');
}

function formatDuration(value) {
    var match = /^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d+))?$/.exec(String(value || ''));
    if (!match) return value || currentStrings().notAvailable;
    var seconds = Number(match[1] || 0) * 86400 + Number(match[2]) * 3600 + Number(match[3]) * 60 + Number(match[4]);
    if (seconds < 60) return (seconds + Number('0.' + (match[5] || '0'))).toFixed(1) + (fontManagementState.locale === 'zh' ? ' 秒' : ' s');
    var minutes = Math.floor(seconds / 60);
    return fontManagementState.locale === 'zh'
        ? (minutes >= 60 ? Math.floor(minutes / 60) + ' 小时 ' : '') + (minutes % 60) + ' 分 ' + (seconds % 60) + ' 秒'
        : (minutes >= 60 ? Math.floor(minutes / 60) + ' h ' : '') + (minutes % 60) + ' min ' + (seconds % 60) + ' s';
}

function getFontUrl(path, query) {
    return ApiClient.getUrl('AssSubsetter/Fonts/' + path, query || {});
}

function normalizeFontResponse(value) {
    if (Array.isArray(value)) {
        return value.map(normalizeFontResponse);
    }

    if (value === null || typeof value !== 'object') {
        return value;
    }

    var normalized = {};
    Object.keys(value).forEach(function (key) {
        var normalizedKey = key.charAt(0).toLowerCase() + key.slice(1);
        normalized[normalizedKey] = normalizeFontResponse(value[key]);
    });
    return normalized;
}

function getFontJson(path, query) {
    return ApiClient.getJSON(getFontUrl(path, query)).then(normalizeFontResponse);
}

function authenticatedFetch(path, options, query) {
    var requestOptions = Object.assign({ credentials: 'same-origin' }, options || {});
    var headers = new Headers(requestOptions.headers || {});
    var token = typeof ApiClient.accessToken === 'function' ? ApiClient.accessToken() : null;
    if (token) {
        headers.set('Authorization', 'MediaBrowser Token="' + token + '"');
    }
    requestOptions.headers = headers;
    return window.fetch(getFontUrl(path, query), requestOptions);
}

function readResponseJson(response) {
    return response.json().catch(function () { return {}; }).then(function (body) {
        body = normalizeFontResponse(body);
        if (!response.ok) {
            throw new Error(body.error || response.statusText || String(response.status));
        }

        return body;
    });
}

function postFontJson(path, body) {
    return authenticatedFetch(path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
    }).then(readResponseJson);
}

function createIdempotencyKey() {
    if (window.crypto && typeof window.crypto.randomUUID === 'function') {
        return window.crypto.randomUUID();
    }

    return 'font-management-' + Date.now() + '-' + Math.random().toString(16).slice(2);
}

function enumName(value, names) {
    return typeof value === 'number' && names[value] ? names[value] : String(value === null || value === undefined ? '' : value);
}

function localizedEnumName(value, rawNames, labels) {
    var index = typeof value === 'number' ? value : rawNames.indexOf(value);
    return index >= 0 && labels[index] ? labels[index] : enumName(value, rawNames);
}

function verificationStatusText(key, value) {
    var status = enumName(value, ['Unknown', 'Passed', 'Failed', 'NotApplicable']);
    var strings = currentStrings();
    var labels = key === 'embeddingRights' ? strings.embeddingStates : strings.verificationStates;
    return labels[status] || status;
}

function decisionName(value) {
    return enumName(value, ['Retained', 'QuarantineCandidate']);
}

function localizeMemberReason(reason) {
    var strings = currentStrings();
    var replacement = /^Directly replaced by retained file ([0-9]+)\.$/.exec(reason || '');
    if (replacement) return formatText(strings.reasonDirectReplacement, replacement[1]);
    var known = {
        'Selected by quality and deterministic tie-breaks.': strings.reasonQuality,
        'Retained because its directory is protected.': strings.reasonProtected,
        'Retained because linked paths are not cleanup candidates.': strings.reasonLinked,
        'Retained because complete classification evidence is unavailable.': strings.reasonIncomplete
    };
    return known[reason] || reason || strings.notAvailable;
}

function localizeCollectionImpact(value) {
    if (!value) return '';
    return String(value).replace(/File ([0-9]+) contains ([0-9]+) Faces; ([0-9]+) other Faces share the file/g,
        function (match, fileId, faceCount, otherCount) {
            return formatText(currentStrings().collectionImpactText, fileId, faceCount, otherCount);
        });
}

function operationStatus(value) {
    return enumName(value, ['Pending', 'Running', 'Complete', 'Failed', 'Cancelled']);
}

function isTerminalOperation(value) {
    var status = operationStatus(value);
    return status === 'Complete' || status === 'Failed' || status === 'Cancelled';
}

function showOperation(operation) {
    var strings = currentStrings();
    var status = operationStatus(operation.status);
    var message = operation.error
        ? formatText(strings.operationFailed, operation.id, status, operation.error)
        : formatText(strings.operationStatus, operation.id, status);
    setElementText('#FontOperationStatus', message);
}

function pollOperation(operation) {
    var generation = ++fontManagementState.pollGeneration;
    showOperation(operation);
    if (isTerminalOperation(operation.status)) {
        return Promise.resolve(operation);
    }

    return new Promise(function (resolve) {
        window.setTimeout(resolve, 1500);
    }).then(function () {
        if (generation !== fontManagementState.pollGeneration) {
            return operation;
        }

        return getFontJson('operations/' + operation.id).then(function (current) {
            if (generation !== fontManagementState.pollGeneration) {
                return current;
            }

            showOperation(current);
            return isTerminalOperation(current.status) ? current : pollOperation(current);
        }).catch(function (error) {
            setElementText('#FontOperationStatus', formatText(currentStrings().errorState, error.message));
            return operation;
        });
    });
}

function queueOperation(path, body) {
    return postFontJson(path, body).then(function (operation) {
        setElementText('#FontOperationStatus', formatText(currentStrings().operationQueued, operation.id, operationStatus(operation.status)));
        return pollOperation(operation);
    }).catch(function (error) {
        setElementText('#FontOperationStatus', formatText(currentStrings().errorState, error.message));
        return null;
    });
}

function formatDate(value) {
    if (!value) return currentStrings().notAvailable;
    var date = new Date(value);
    return isNaN(date.getTime()) ? String(value) : date.toLocaleString();
}

function formatBytes(value) {
    var bytes = Number(value || 0);
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1048576) return (bytes / 1024).toFixed(1) + ' KiB';
    if (bytes < 1073741824) return (bytes / 1048576).toFixed(1) + ' MiB';
    return (bytes / 1073741824).toFixed(1) + ' GiB';
}

function pageCount(total) {
    return Math.max(1, Math.ceil(total / Math.min(200, fontManagementState.pageSize)));
}

function renderPageStatus(selector, page, total) {
    var strings = currentStrings();
    setElementText(selector, formatText(strings.pageStatus, page, formatNumber(total)));
    var select = document.querySelector(selector.replace('PageStatus', 'PageSelect'));
    var count = pageCount(total);
    clearElement(select);
    for (var number = 1; number <= count; number++) {
        var option = document.createElement('option');
        option.value = String(number);
        option.textContent = formatText(strings.pageNumber, number);
        select.appendChild(option);
    }
    select.value = String(page);
    select.disabled = count <= 1;
}

function bindPageSelect(selector, pageProperty, totalProperty, load) {
    document.querySelector(selector).addEventListener('change', function (event) {
        var page = Number(event.target.value);
        if (!Number.isInteger(page) || page < 1 || page > pageCount(fontManagementState[totalProperty]) || page === fontManagementState[pageProperty]) return;
        fontManagementState[pageProperty] = page;
        return load();
    });
}

function loadFontOverview() {
    var strings = currentStrings();
    setViewState('#FontOverviewState', 'loading', strings.loadingState);
    return getFontJson('overview').then(function (overview) {
        var list = document.querySelector('#FontOverviewValues');
        clearElement(list);
        appendDefinition(list, strings.overviewFiles, formatNumber(overview.publishedFileCount));
        appendDefinition(list, strings.overviewFaces, formatNumber(overview.publishedFaceCount));
        appendDefinition(list, strings.overviewGeneration, overview.publishedGenerationId == null ? strings.notAvailable : overview.publishedGenerationId);
        appendDefinition(list, strings.overviewAnalysis, overview.latestAnalysis ? overview.latestAnalysis.id : strings.notAvailable);
        appendDefinition(list, strings.overviewCandidates, formatNumber(overview.latestAnalysis ? overview.latestAnalysis.candidateCount : 0));
        appendDefinition(list, strings.overviewGroups, formatNumber(overview.latestAnalysis ? overview.latestAnalysis.groupCount : 0));
        appendDefinition(list, strings.overviewEligible, formatNumber(overview.eligibleQuarantineCount));
        appendDefinition(list, strings.overviewBytes, formatBytes(overview.reclaimableBytes));

        if (overview.lastScan) {
            appendDefinition(list, overview.lastScan.changed ? strings.lastScanChanged : strings.lastScanUnchanged, '#' + overview.lastScan.generationId + ' / #' + overview.lastScan.analysisBatchId);
            var changeReasons = overview.lastScan.changeReasons || [];
            if (changeReasons.length) appendDefinition(list, strings.overviewScanChanges, changeReasons.map(function (reason) { return strings.staleReasonLabels[reason] || reason; }).join(' · '), true);
        }
        if (overview.latestAnalysis) appendDefinition(list, strings.overviewAnalysisValidity, analysisValidityText(overview.latestAnalysis), true);
        renderDatabase(overview.databaseHealth || {}, overview.phaseTimings || []);
        fontManagementState.analysisBatch = overview.latestAnalysis || null;
        fontManagementState.analysisBatchId = overview.latestAnalysis ? overview.latestAnalysis.id : null;
        setViewState('#FontOverviewState', 'ready', '');
        document.querySelector('#ExportAnalysisButton').disabled = !overview.latestAnalysis;
        return overview;
    }).catch(function (error) {
        setViewState('#FontOverviewState', 'error', formatText(strings.errorState, error.message));
    });
}

function analysisValidityText(batch) {
    var strings = currentStrings();
    var parts = [batch.isCurrent === true ? strings.analysisCurrent : strings.analysisStale];
    parts = parts.concat((batch.staleReasons || []).map(function (reason) { return strings.staleReasonLabels[reason] || reason; }));
    if (batch.generationId != null) parts.push(strings.overviewGeneration + ' #' + batch.generationId);
    return parts.join(' · ');
}

function populateAnalysisBatches(page) {
    var select = document.querySelector('#AnalysisBatchSelect');
    clearElement(select);
    (page.items || []).forEach(function (batch) {
        var option = document.createElement('option');
        option.value = String(batch.id);
        option.textContent = '#' + batch.id + ' · ' + localizedEnumName(batch.status, ['Running', 'Complete', 'Stale', 'Failed'], currentStrings().analysisStatuses) + ' · ' + analysisValidityText(batch) + ' · ' + formatDate(batch.completedUtc || batch.startedUtc);
        option.dataset.batch = JSON.stringify(batch);
        select.appendChild(option);
    });
    if (page.items && page.items.length) {
        var selected = page.items.find(function (batch) { return batch.id === fontManagementState.analysisBatchId; }) || page.items[0];
        if (selected.id !== fontManagementState.analysisBatchId) {
            fontManagementState.selectedFileIds.clear();
            fontManagementState.reportPage = 1;
        }
        select.value = String(selected.id);
        fontManagementState.analysisBatchId = selected.id;
        fontManagementState.analysisBatch = selected;
    }
}

function loadFontAnalysis() {
    var strings = currentStrings();
    setViewState('#FontReportState', 'loading', strings.loadingState);
    return getFontJson('analysis-batches', { page: 1, pageSize: Math.min(200, fontManagementState.pageSize) }).then(function (page) {
        populateAnalysisBatches(page);
        return loadAnalysisReports();
    }).catch(function (error) {
        setViewState('#FontReportState', 'error', formatText(strings.errorState, error.message));
    });
}

function updateReportSelection() {
    var selected = fontManagementState.selectedFileIds;
    setElementText('#ReportSelectionStatus', formatText(currentStrings().selectedFilesStatus, selected.size));
    document.querySelector('#QuarantineSelectedButton').disabled = !selected.size || !fontManagementState.analysisBatch || fontManagementState.analysisBatch.isCurrent !== true;
    document.querySelector('#ClearReportSelectionButton').disabled = !selected.size;
    document.querySelector('#QuarantineAllVerifiedButton').disabled = !fontManagementState.analysisBatch || fontManagementState.analysisBatch.isCurrent !== true;
    document.querySelectorAll('[data-report-file-id]').forEach(function (checkbox) {
        checkbox.checked = selected.has(Number(checkbox.getAttribute('data-report-file-id')));
    });
    document.querySelectorAll('[data-report-plan-files]').forEach(function (button) {
        var fileIds = JSON.parse(button.getAttribute('data-report-plan-files'));
        var allSelected = fileIds.every(function (id) { return selected.has(id); });
        button.textContent = allSelected ? currentStrings().deselectPlan : currentStrings().selectPlan;
        button.setAttribute('aria-pressed', String(allSelected));
    });
}

function renderReplacementReport(item) {
    var strings = currentStrings();
    var plan = item.replacement;
    var files = plan.files || [];
    var retained = files.filter(function (file) { return decisionName(file.decision) !== 'QuarantineCandidate'; });
    var candidates = files.filter(function (file) { return decisionName(file.decision) === 'QuarantineCandidate'; });
    var card = document.createElement('article');
    card.className = 'fontReportCard fontReportCard--replacement';
    var header = document.createElement('div');
    header.className = 'fontCardHeader';
    var title = document.createElement('h3');
    title.className = 'fontCardTitle';
    title.textContent = strings.replacementTitle;
    header.appendChild(title);
    appendBadge(header, formatText(strings.groupReclaimable, formatBytes(plan.reclaimableBytes)), false);
    var meta = document.createElement('span');
    meta.className = 'fontCardMeta';
    meta.textContent = strings.batchColumn + ' #' + item.analysisBatchId;
    header.appendChild(meta);
    card.appendChild(header);
    var overview = document.createElement('p');
    overview.className = 'fontReplacementSummary';
    overview.textContent = formatText(strings.replacementSummary, retained.length, candidates.length);
    card.appendChild(overview);
    var kinds = document.createElement('div');
    kinds.className = 'fontReplacementKinds';
    (plan.relationshipKinds || []).forEach(function (kind) { appendBadge(kinds, relationshipName(kind), false); });
    card.appendChild(kinds);
    var columns = document.createElement('div');
    columns.className = 'fontReplacementGrid';
    function appendFiles(items, headingText, candidate) {
        var section = document.createElement('section');
        section.className = 'fontReplacementColumn';
        var heading = document.createElement('h4');
        heading.textContent = headingText;
        section.appendChild(heading);
        var list = document.createElement('div');
        list.className = 'fontReplacementFiles';
        function appendFile(parent, file) {
            var entry = document.createElement('div');
            entry.className = 'fontReplacementFile';
            if (candidate) {
                var selection = document.createElement('label');
                selection.className = 'fontFileSelection';
                var checkbox = document.createElement('input');
                checkbox.type = 'checkbox';
                checkbox.checked = fontManagementState.selectedFileIds.has(file.fileId);
                checkbox.setAttribute('data-report-file-id', String(file.fileId));
                var filename = (file.path || '').split(/[\\/]/).pop();
                checkbox.setAttribute('aria-label', strings.selectColumn + ' ' + filename);
                checkbox.addEventListener('change', function () {
                    if (checkbox.checked) fontManagementState.selectedFileIds.add(file.fileId);
                    else fontManagementState.selectedFileIds.delete(file.fileId);
                    updateReportSelection();
                });
                selection.appendChild(checkbox);
                var label = document.createElement('span');
                label.textContent = strings.selectColumn + ' · ' + formatText(strings.groupFaceCount, file.faceCount);
                selection.appendChild(label);
                entry.appendChild(selection);
            } else {
                appendBadge(entry, formatText(strings.groupFaceCount, file.faceCount), false);
            }
            appendPath(entry, '', file.path);
            (file.reasons || []).forEach(function (reason) {
                var note = document.createElement('p');
                note.className = 'fontGroupFileMeta';
                note.textContent = localizeMemberReason(reason);
                entry.appendChild(note);
            });
            parent.appendChild(entry);
        }
        items.slice(0, 3).forEach(function (file) { appendFile(list, file); });
        if (items.length > 3) {
            var more = document.createElement('details');
            more.className = 'fontDisclosure';
            var summary = document.createElement('summary');
            summary.textContent = formatText(strings.moreFiles, items.length - 3);
            more.appendChild(summary);
            items.slice(3).forEach(function (file) { appendFile(more, file); });
            list.appendChild(more);
        }
        section.appendChild(list);
        columns.appendChild(section);
    }
    appendFiles(retained, strings.retainedFilesTitle, false);
    appendFiles(candidates, strings.candidateFilesTitle, true);
    card.appendChild(columns);
    var actions = document.createElement('div');
    actions.className = 'fontActionBar';
    var selectPlan = document.createElement('button');
    selectPlan.type = 'button';
    selectPlan.setAttribute('is', 'emby-button');
    selectPlan.className = 'raised emby-button';
    var candidateIds = candidates.map(function (file) { return file.fileId; });
    selectPlan.setAttribute('data-report-plan-files', JSON.stringify(candidateIds));
    var allSelected = candidateIds.every(function (id) { return fontManagementState.selectedFileIds.has(id); });
    selectPlan.textContent = allSelected ? strings.deselectPlan : strings.selectPlan;
    selectPlan.setAttribute('aria-pressed', String(allSelected));
    selectPlan.addEventListener('click', function () {
        var selected = candidateIds.every(function (id) { return fontManagementState.selectedFileIds.has(id); });
        candidateIds.forEach(function (id) {
            if (selected) fontManagementState.selectedFileIds.delete(id);
            else fontManagementState.selectedFileIds.add(id);
        });
        updateReportSelection();
    });
    actions.appendChild(selectPlan);
    card.appendChild(actions);
    var details = document.createElement('details');
    details.className = 'fontDisclosure';
    var summary = document.createElement('summary');
    summary.textContent = strings.replacementEvidence;
    details.appendChild(summary);
    var target = document.createElement('div');
    target.className = 'fontDetailContent';
    details.appendChild(target);
    details.addEventListener('toggle', function () {
        if (!details.open || details.dataset.loaded || details.dataset.loading) return;
        details.dataset.loading = 'true';
        loadGroupDetail(item.analysisBatchId, plan.groupId, target).then(function (loaded) {
            if (loaded) details.dataset.loaded = 'true';
            delete details.dataset.loading;
        });
    });
    card.appendChild(details);
    return card;
}

function renderAnalysisReports(page) {
    var strings = currentStrings();
    var list = document.querySelector('#analysis-report-rows');
    clearElement(list);
    (page.items || []).forEach(function (item) {
        if (item.replacement) {
            list.appendChild(renderReplacementReport(item));
            return;
        }
        var card = document.createElement('article');
        card.className = 'fontReportCard';
        var header = document.createElement('div');
        header.className = 'fontCardHeader';
        var title = document.createElement('h3');
        title.className = 'fontCardTitle';
        title.textContent = item.isUnclassifiable ? (item.relationId == null ? strings.unclassifiableFileState : strings.unclassifiablePairState) : relationshipName(item.relationshipKind);
        header.appendChild(title);
        appendBadge(header, actionName(item.action), item.action === 3 || item.action === 'Conflict' || item.action === 'conflict');
        var batch = document.createElement('span');
        batch.className = 'fontCardMeta';
        batch.textContent = strings.batchColumn + ' #' + item.analysisBatchId;
        header.appendChild(batch);
        card.appendChild(header);

        var paths = document.createElement('div');
        paths.className = 'fontPathGrid';
        var pairPaths = item.pairEvidence && item.pairEvidence.leftFace && item.pairEvidence.rightFace
            ? [item.pairEvidence.leftFace.path, item.pairEvidence.rightFace.path]
            : item.paths || [];
        pairPaths.forEach(function (path, index) {
            appendPath(paths, formatText(strings.reportPath, index + 1), path);
        });
        card.appendChild(paths);
        renderPairSummary(card, item);
        if (item.isUnclassifiable && !item.pairEvidence) {
            var fileDiagnostic = document.createElement('div');
            fileDiagnostic.className = 'fontDetailGrid fontPairSummary';
            appendDetail(fileDiagnostic, strings.diagnosticLabel, formatDiagnostic(item.diagnostic || strings.missingDiagnosticHint));
            card.appendChild(fileDiagnostic);
            var fileGuidance = document.createElement('p');
            fileGuidance.className = 'fontPairGuidance';
            fileGuidance.textContent = strings.handlingAdvice + '：' + strings.unclassifiableFileGuidance;
            card.appendChild(fileGuidance);
        }

        var details = document.createElement('details');
        details.className = 'fontDisclosure';
        var summary = document.createElement('summary');
        summary.textContent = strings.showEvidence;
        details.appendChild(summary);
        details.addEventListener('toggle', function () {
            if (details.open && !details.dataset.loaded) {
                renderReportEvidence(details, item);
                details.dataset.loaded = 'true';
            }
        });
        card.appendChild(details);
        list.appendChild(card);
    });
    updateReportSelection();
    fontManagementState.reportTotal = Number(page.totalCount || 0);
    renderPageStatus('#ReportPageStatus', page.page || fontManagementState.reportPage, fontManagementState.reportTotal);
    document.querySelector('#ReportPreviousPage').disabled = fontManagementState.reportPage <= 1;
    document.querySelector('#ReportNextPage').disabled = fontManagementState.reportPage * Math.min(200, fontManagementState.pageSize) >= fontManagementState.reportTotal;
    setViewState('#FontReportState', fontManagementState.reportTotal ? 'ready' : 'empty', fontManagementState.reportTotal ? '' : strings.emptyState);
}

function renderReportEvidence(details, item) {
    var strings = currentStrings();
    var content = document.createElement('div');
    content.className = 'fontDetailContent';
    if ((item.directories || []).length || (item.technologies || []).length) {
        var metadata = document.createElement('div');
        metadata.className = 'fontDetailGrid';
        if ((item.directories || []).length) appendDetail(metadata, strings.directoryFilter, item.directories.join(' · '));
        if ((item.technologies || []).length) appendDetail(metadata, strings.technologyFilter, item.technologies.join(', '));
        content.appendChild(metadata);
    }

    if (item.diagnostic) {
        var diagnostic = document.createElement('p');
        diagnostic.className = 'fontDiagnostic';
        diagnostic.textContent = strings.diagnosticLabel + '：' + formatDiagnostic(item.diagnostic);
        content.appendChild(diagnostic);
    }

    var pair = item.pairEvidence;
    if (pair) {
        var comparisonTitle = document.createElement('h4');
        comparisonTitle.textContent = strings.comparisonEvidence;
        content.appendChild(comparisonTitle);
        var comparison = document.createElement('div');
        comparison.className = 'fontDetailGrid';
        var contentAreas = (pair.differenceTypes || []).filter(function (value) { return !!strings.semanticDifferenceLabels[reportKindKey(value)]; });
        if (contentAreas.length) appendDetail(comparison, strings.differenceTypes, contentAreas.map(semanticDifferenceName).join(', '));
        if (pair.sharedNames && pair.sharedNames.length) appendDetail(comparison, strings.sharedNamesLabel, pair.sharedNames.join(' · '));
        faceFieldDifferences(pair.leftFace, pair.rightFace).forEach(function (field) { appendDetail(comparison, field[0], field[1]); });
        appendDetail(comparison, strings.differingCodePoints, (pair.differingCodePoints || []).map(function (point) {
            return String(point) + ' (U+' + Number(point).toString(16).toUpperCase().padStart(4, '0') + ')';
        }).join(', '));
        appendDetail(comparison, formatText(strings.fileCanReplace, 1, 2), pair.leftCanReplaceRight ? strings.yesValue : strings.noValue);
        appendDetail(comparison, formatText(strings.fileCanReplace, 2, 1), pair.rightCanReplaceLeft ? strings.yesValue : strings.noValue);
        if (pair.collectionFaceImpact) appendDetail(comparison, strings.collectionFaceImpact, localizeCollectionImpact(pair.collectionFaceImpact));
        content.appendChild(comparison);
        var faceGrid = document.createElement('div');
        faceGrid.className = 'fontScoreGrid';
        [pair.leftFace, pair.rightFace].forEach(function (face, index) {
            if (!face) return;
            var faceCard = document.createElement('div');
            faceCard.className = 'fontScoreCard';
            var faceTitle = document.createElement('h5');
            faceTitle.textContent = formatText(strings.pairFace, index + 1, face.faceId);
            faceCard.appendChild(faceTitle);
            var values = document.createElement('div');
            values.className = 'fontDetailGrid';
            appendDetail(values, strings.familyLabel, face.family);
            appendDetail(values, strings.styleLabel, face.style);
            appendDetail(values, strings.weightLabel, face.weight);
            appendDetail(values, strings.widthLabel, face.width);
            appendDetail(values, strings.italicLabel, face.italic ? strings.yesValue : strings.noValue);
            appendDetail(values, strings.postScriptLabel, face.postScriptName);
            appendDetail(values, strings.versionLabel, face.version);
            appendDetail(values, strings.digestLabel, face.strictFingerprint);
            var reason = index === 0 ? pair.leftDiagnostic : pair.rightDiagnostic;
            if (reason) appendDetail(values, strings.diagnosticLabel, formatDiagnostic(reason));
            faceCard.appendChild(values);
            faceGrid.appendChild(faceCard);
        });
        content.appendChild(faceGrid);
    }

    if (item.scores && item.scores.length) {
        var scoresTitle = document.createElement('h4');
        scoresTitle.textContent = strings.scoreBreakdown;
        content.appendChild(scoresTitle);
        var scoreHints = document.createElement('div');
        scoreHints.className = 'fontEvidenceNotes';
        [strings.verificationHint, strings.embeddingHint, strings.modifiedScoreHint].forEach(function (hint) {
            var note = document.createElement('p');
            note.textContent = hint;
            scoreHints.appendChild(note);
        });
        content.appendChild(scoreHints);
        var scoreGrid = document.createElement('div');
        scoreGrid.className = 'fontScoreGrid';
        item.scores.forEach(function (score) {
            var scoreCard = document.createElement('div');
            scoreCard.className = 'fontScoreCard';
            var title = document.createElement('h5');
            title.textContent = 'Face #' + score.faceId + ' · ' + formatNumber(score.retentionScore);
            scoreCard.appendChild(title);
            var components = document.createElement('div');
            components.className = 'fontDetailGrid';
            Object.keys(strings.scoreComponents).forEach(function (key) {
                appendDetail(components, strings.scoreComponents[key], (score.components || {})[key]);
            });
            scoreCard.appendChild(components);
            var verificationTitle = document.createElement('h5');
            verificationTitle.textContent = strings.verificationDetails;
            scoreCard.appendChild(verificationTitle);
            var verificationList = document.createElement('ul');
            var deferred = [];
            var notApplicable = [];
            Object.entries(score.verification || {}).forEach(function (entry) {
                if (!Object.prototype.hasOwnProperty.call(strings.verificationLabels, entry[0])) {
                    return;
                }
                var state = enumName(entry[1], ['Unknown', 'Passed', 'Failed', 'NotApplicable']);
                if (state === 'Unknown' && entry[0] !== 'embeddingRights') {
                    deferred.push(strings.verificationLabels[entry[0]] || entry[0]);
                    return;
                }
                if (state === 'NotApplicable') {
                    notApplicable.push(strings.verificationLabels[entry[0]] || entry[0]);
                    return;
                }
                var listItem = document.createElement('li');
                listItem.dataset.state = state.toLowerCase();
                listItem.textContent = (strings.verificationLabels[entry[0]] || entry[0]) + '：' + verificationStatusText(entry[0], entry[1]);
                verificationList.appendChild(listItem);
            });
            scoreCard.appendChild(verificationList);
            if (deferred.length) {
                var deferredText = document.createElement('p');
                deferredText.className = 'fontDeferredChecks';
                deferredText.textContent = strings.deferredVerification + '：' + deferred.join('、');
                scoreCard.appendChild(deferredText);
            }
            if (notApplicable.length) {
                var optionalText = document.createElement('p');
                optionalText.className = 'fontDeferredChecks';
                optionalText.textContent = strings.notApplicableVerification + '：' + notApplicable.join('、');
                scoreCard.appendChild(optionalText);
            }
            scoreGrid.appendChild(scoreCard);
        });
        content.appendChild(scoreGrid);
    }

    if (item.retainedRationale && item.retainedRationale.length) {
        var rationaleTitle = document.createElement('h4');
        rationaleTitle.textContent = strings.retainedRationale;
        content.appendChild(rationaleTitle);
        var rationaleList = document.createElement('ul');
        item.retainedRationale.forEach(function (reason) {
            var listItem = document.createElement('li');
            listItem.textContent = reason;
            rationaleList.appendChild(listItem);
        });
        content.appendChild(rationaleList);
    }
    details.appendChild(content);
}

function loadAnalysisReports() {
    var loadId = ++fontManagementState.reportLoadId;
    if (!fontManagementState.analysisBatchId) {
        renderAnalysisReports({ items: [], page: 1, totalCount: 0 });
        return Promise.resolve();
    }
    setViewState('#FontReportState', 'loading', currentStrings().loadingState);
    return getFontJson('reports', {
        page: fontManagementState.reportPage,
        pageSize: Math.min(200, fontManagementState.pageSize),
        analysisBatchId: fontManagementState.analysisBatchId,
        relationship: document.querySelector('#ReportRelationshipFilter').value,
        unclassifiable: document.querySelector('#ReportUnclassifiableFilter').value,
        directory: document.querySelector('#ReportDirectoryFilter').value,
        technology: document.querySelector('#ReportTechnologyFilter').value,
        action: document.querySelector('#ReportActionFilter').value
    }).then(function (page) {
        if (loadId !== fontManagementState.reportLoadId) return;
        renderAnalysisReports(page);
    }).catch(function (error) {
        if (loadId !== fontManagementState.reportLoadId) return;
        setViewState('#FontReportState', 'error', formatText(currentStrings().errorState, error.message));
    });
}

function renderGroupDetail(detail, target) {
    var strings = currentStrings();
    clearElement(target);
    var group = detail.group || {};
    var members = group.members || [];
    var pairs = detail.pairs || [];
    var faces = new Map();
    pairs.forEach(function (pair) {
        if (pair.leftFace) faces.set(pair.leftFaceId, pair.leftFace);
        if (pair.rightFace) faces.set(pair.rightFaceId, pair.rightFace);
    });
    var files = new Map();
    members.forEach(function (member) {
        if (member.face) faces.set(member.faceId, member.face);
        if (!files.has(member.fileId)) files.set(member.fileId, []);
        files.get(member.fileId).push(member);
    });

    var header = document.createElement('div');
    header.className = 'fontCardHeader';
    var heading = document.createElement('h3');
    heading.className = 'fontCardTitle';
    heading.textContent = formatText(strings.groupHeading, group.id, files.size, members.length);
    header.appendChild(heading);
    if (group.reclaimableBytes) appendBadge(header, formatText(strings.groupReclaimable, formatBytes(group.reclaimableBytes)), false);
    target.appendChild(header);

    var filesTitle = document.createElement('h4');
    filesTitle.className = 'fontGroupSectionTitle';
    filesTitle.textContent = strings.groupFilesTitle;
    target.appendChild(filesTitle);
    var fileGrid = document.createElement('div');
    fileGrid.className = 'fontGroupFileGrid';
    files.forEach(function (fileMembers, fileId) {
        var card = document.createElement('div');
        card.className = 'fontGroupFileCard';
        var candidate = fileMembers.every(function (member) {
            return decisionName(member.decision) === 'QuarantineCandidate';
        });
        var decision = localizedEnumName(candidate ? 'QuarantineCandidate' : 'Retained', ['Retained', 'QuarantineCandidate'], strings.decisionLabels);
        appendBadge(card, decision, candidate);
        var knownFace = fileMembers.map(function (member) { return faces.get(member.faceId); }).find(Boolean);
        appendPath(card, formatText(strings.fileNumber, fileId), knownFace && knownFace.path);
        var faceLabel = document.createElement('span');
        faceLabel.className = 'fontDetailLabel';
        faceLabel.textContent = strings.groupFacesLabel;
        card.appendChild(faceLabel);
        var faceList = document.createElement('ul');
        fileMembers.forEach(function (member) {
            var face = faces.get(member.faceId);
            var item = document.createElement('li');
            item.textContent = (face ? [face.family, face.style].filter(Boolean).join(' · ') + ' · ' : '') + 'Face #' + member.faceId;
            faceList.appendChild(item);
        });
        card.appendChild(faceList);
        var reasons = Array.from(new Set(fileMembers.map(function (member) { return localizeMemberReason(member.reason); })));
        reasons.forEach(function (reason) {
            var note = document.createElement('p');
            note.className = 'fontGroupFileMeta';
            note.textContent = strings.reasonColumn + '：' + reason;
            card.appendChild(note);
        });
        var scoreDetails = document.createElement('details');
        scoreDetails.className = 'fontDisclosure';
        var scoreSummary = document.createElement('summary');
        scoreSummary.textContent = strings.scoreBreakdown + ' · ' + Array.from(new Set(fileMembers.map(function (member) { return member.retentionScore; }))).join(' / ');
        scoreDetails.appendChild(scoreSummary);
        fileMembers.forEach(function (member) {
            var scoreGrid = document.createElement('div');
            scoreGrid.className = 'fontDetailGrid';
            var scoreTitle = document.createElement('h5');
            scoreTitle.textContent = 'Face #' + member.faceId + ' · ' + member.retentionScore;
            scoreDetails.appendChild(scoreTitle);
            Object.keys(strings.scoreComponents).forEach(function (key) {
                appendDetail(scoreGrid, strings.scoreComponents[key], (member.score || {})[key]);
            });
            scoreDetails.appendChild(scoreGrid);
        });
        card.appendChild(scoreDetails);
        fileGrid.appendChild(card);
    });
    target.appendChild(fileGrid);

    var pairTitle = document.createElement('h4');
    pairTitle.className = 'fontGroupSectionTitle';
    pairTitle.textContent = formatText(strings.groupPairsTitle, pairs.length);
    target.appendChild(pairTitle);
    if (!pairs.length) {
        var empty = document.createElement('p');
        empty.textContent = strings.groupNoPairs;
        target.appendChild(empty);
    }
    var pairList = document.createElement('div');
    pairList.className = 'fontGroupPairList';
    function appendPair(parent, pair) {
        var item = document.createElement('details');
        item.className = 'fontDisclosure fontGroupPair';
        var summary = document.createElement('summary');
        var leftName = pair.leftFace && pair.leftFace.path ? pair.leftFace.path.split(/[\\/]/).pop() : 'Face #' + pair.leftFaceId;
        var rightName = pair.rightFace && pair.rightFace.path ? pair.rightFace.path.split(/[\\/]/).pop() : 'Face #' + pair.rightFaceId;
        summary.textContent = leftName + ' ↔ ' + rightName + ' · ' + relationshipName(pair.relationshipKind);
        item.appendChild(summary);
        item.addEventListener('toggle', function () {
            if (item.open && !item.dataset.loaded) {
                renderReportEvidence(item, { pairEvidence: pair });
                item.dataset.loaded = 'true';
            }
        });
        parent.appendChild(item);
    }
    pairs.filter(function (pair) { return pair.leftCanReplaceRight || pair.rightCanReplaceLeft; }).forEach(function (pair) {
        appendPair(pairList, pair);
    });
    if (pairList.childNodes.length) target.appendChild(pairList);
    var otherPairs = pairs.filter(function (pair) { return !pair.leftCanReplaceRight && !pair.rightCanReplaceLeft; });
    if (otherPairs.length) {
        var other = document.createElement('details');
        other.className = 'fontDisclosure';
        var otherSummary = document.createElement('summary');
        otherSummary.textContent = formatText(strings.groupOtherPairs, otherPairs.length);
        other.appendChild(otherSummary);
        var otherList = document.createElement('div');
        otherList.className = 'fontGroupPairList';
        otherPairs.forEach(function (pair) { appendPair(otherList, pair); });
        other.appendChild(otherList);
        target.appendChild(other);
    }

    var constraints = detail.hardConstraints || [];
    var reasons = detail.skipReasons || [];
    if (constraints.length || reasons.length) {
        var technical = document.createElement('details');
        technical.className = 'fontDisclosure fontGroupTechnical';
        var technicalSummary = document.createElement('summary');
        technicalSummary.textContent = strings.groupTechnicalTitle;
        technical.appendChild(technicalSummary);
        if (constraints.length) {
            var constraintTitle = document.createElement('h4');
            constraintTitle.textContent = strings.groupConstraintsTitle;
            technical.appendChild(constraintTitle);
            var constraintList = document.createElement('ul');
            constraints.forEach(function (constraint) {
                var item = document.createElement('li');
                item.textContent = strings.constraintLabels[constraint] || constraint;
                constraintList.appendChild(item);
            });
            technical.appendChild(constraintList);
        }
        if (reasons.length) {
            var reasonTitle = document.createElement('h4');
            reasonTitle.textContent = strings.groupReasonsTitle;
            technical.appendChild(reasonTitle);
            var reasonList = document.createElement('ul');
            reasons.forEach(function (reason) {
                var item = document.createElement('li');
                item.textContent = localizeMemberReason(reason);
                reasonList.appendChild(item);
            });
            technical.appendChild(reasonList);
        }
        target.appendChild(technical);
    }
}

function loadGroupDetail(batchId, groupId, target) {
    clearElement(target);
    target.textContent = currentStrings().loadingState;
    return getFontJson('analysis-batches/' + batchId + '/groups/' + groupId).then(function (detail) {
        target.textContent = '';
        renderGroupDetail(detail, target);
        return true;
    }).catch(function (error) {
        target.textContent = formatText(currentStrings().errorState, error.message);
        return false;
    });
}

function loadAllVerifiedCandidatePage(batchId, pageNumber, candidateFileIds, reclaimableBytes) {
    var pageSize = Math.min(200, 200);
    return getFontJson('groups', {
        page: pageNumber,
        pageSize: pageSize,
        analysisBatchId: batchId,
        decision: 'quarantineCandidate',
        sort: 'reclaimableBytes'
    }).then(function (page) {
        (page.items || []).forEach(function (group) {
            reclaimableBytes.value += Number(group.reclaimableBytes || 0);
            (group.members || []).forEach(function (member) {
                if (decisionName(member.decision) === 'QuarantineCandidate') {
                    candidateFileIds.add(member.fileId);
                }
            });
        });
        if (pageNumber * pageSize < Number(page.totalCount || 0)) {
            return loadAllVerifiedCandidatePage(batchId, pageNumber + 1, candidateFileIds, reclaimableBytes);
        }

        return { candidateFileIds: candidateFileIds, reclaimableBytes: reclaimableBytes.value };
    });
}

function confirmAllVerified() {
    if (!fontManagementState.analysisBatch || fontManagementState.analysisBatch.isCurrent !== true) {
        setElementText('#FontOperationStatus', currentStrings().renameStale);
        return Promise.resolve();
    }
    var strings = currentStrings();
    var batchId = fontManagementState.analysisBatchId;
    if (!batchId || !window.confirm(strings.confirmAllVerifiedFirst)) return Promise.resolve();
    setViewState('#FontReportState', 'loading', strings.loadingState);
    var candidateFileIds = new Set();
    var bytesAccumulator = { value: 0 };
    return loadAllVerifiedCandidatePage(batchId, 1, candidateFileIds, bytesAccumulator).then(function (result) {
        var itemCount = candidateFileIds.size;
        var bytes = fontManagementState.analysisBatch && fontManagementState.analysisBatch.id === batchId
            ? Number(fontManagementState.analysisBatch.reclaimableBytes || result.reclaimableBytes)
            : result.reclaimableBytes;
        setViewState('#FontReportState', itemCount ? 'ready' : 'empty', itemCount ? '' : strings.emptyState);
        if (!itemCount) return;
        var confirmation = formatNamedText(strings.confirmAllVerified, {
            batchId: batchId,
            itemCount: itemCount,
            bytes: bytes
        });
        if (!window.confirm(confirmation)) return;
        return queueOperation('operations/quarantine/all-verified', {
            batchId: batchId,
            confirmAllVerified: true,
            idempotencyKey: createIdempotencyKey()
        });
    }).catch(function (error) {
        setViewState('#FontReportState', 'error', formatText(strings.errorState, error.message));
    });
}

var quarantineStatusNames = ['Pending', 'Staged', 'Quarantined', 'Restored', 'Purged', 'EvidenceDrift', 'ReplacementMissing', 'DestinationOccupied', 'FailedVerification', 'FailedIo', 'Cancelled', 'Restoring', 'Promoted'];
function quarantineErrorText(error, strings) {
    if (!error) return '';
    if (error.indexOf('permission-denied:') === 0) return strings.quarantinePermissionDenied;
    if (error.indexOf('io-failure:') === 0) return strings.quarantineIoFailure + '：' + error.slice('io-failure:'.length).trim();
    return error;
}

function renderQuarantineItems(items) {
    var strings = currentStrings();
    var list = document.querySelector('#FontQuarantineRows');
    var entries = items || [];
    clearElement(list);
    fontManagementState.quarantineSelections.clear();
    entries.forEach(function (item) {
        var status = enumName(item.status, quarantineStatusNames);
        var canRestore = status === 'Quarantined';
        var card = document.createElement('article');
        card.className = 'fontQuarantineCard' + (item.error ? ' fontQuarantineCard--attention' : '');
        var header = document.createElement('div');
        header.className = 'fontCardHeader';
        if (canRestore) {
            var checkbox = document.createElement('input');
            checkbox.type = 'checkbox';
            checkbox.setAttribute('aria-label', strings.selectColumn + ' ' + formatText(strings.fileNumber, item.fileId));
            checkbox.addEventListener('change', function () {
                if (checkbox.checked) fontManagementState.quarantineSelections.set(item.id, { batchId: item.maintenanceBatchId, fileId: item.fileId });
                else fontManagementState.quarantineSelections.delete(item.id);
            });
            header.appendChild(checkbox);
        }
        var title = document.createElement('h3');
        title.className = 'fontCardTitle';
        title.textContent = (item.sourcePath || '').split(/[\\/]/).pop() || formatText(strings.fileNumber, item.fileId);
        header.appendChild(title);
        appendBadge(header, localizedEnumName(item.status, quarantineStatusNames, strings.quarantineStatuses), !!item.error);
        card.appendChild(header);
        var paths = document.createElement('div');
        paths.className = 'fontPathGrid';
        appendPath(paths, strings.oldPathColumn, item.sourcePath);
        if (item.quarantinePath && status !== 'Pending' && status !== 'Cancelled') {
            appendPath(paths, status === 'Quarantined' || status === 'Restoring' ? strings.quarantinePath : strings.quarantineTargetPath, item.quarantinePath);
        }
        card.appendChild(paths);
        var meta = document.createElement('div');
        meta.className = 'fontQuarantineMeta';
        var metaValues = [formatText(strings.fileNumber, item.fileId)];
        if (item.expiresUtc) metaValues.push(strings.expiryColumn + '：' + formatDate(item.expiresUtc));
        metaValues.forEach(function (value) {
            var part = document.createElement('span');
            part.textContent = value;
            meta.appendChild(part);
        });
        card.appendChild(meta);
        if (item.error) {
            var error = document.createElement('p');
            error.className = 'fontDiagnostic';
            error.textContent = strings.errorColumn + '：' + quarantineErrorText(item.error, strings);
            card.appendChild(error);
        }
        list.appendChild(card);
    });
    setViewState('#FontQuarantineState', entries.length ? 'ready' : 'empty', entries.length ? '' : strings.emptyState);
}

function loadFontQuarantine() {
    var strings = currentStrings();
    var loadId = ++fontManagementState.quarantineLoadId;
    ['quarantined', 'pending', 'failed'].forEach(function (status) {
        var id = '#Quarantine' + status.charAt(0).toUpperCase() + status.slice(1) + 'Tab';
        document.querySelector(id).setAttribute('aria-pressed', status === fontManagementState.quarantineStatus ? 'true' : 'false');
    });
    document.querySelector('.fontQuarantineTabs').setAttribute('aria-label', strings.quarantineStatusFilter);
    document.querySelector('#RestoreSelectedButton').hidden = fontManagementState.quarantineStatus !== 'quarantined';
    setViewState('#FontQuarantineState', 'loading', strings.loadingState);
    return getFontJson('quarantine/items', {
        page: fontManagementState.quarantinePage,
        pageSize: Math.min(200, fontManagementState.pageSize),
        status: fontManagementState.quarantineStatus
    }).then(function (page) {
        if (loadId !== fontManagementState.quarantineLoadId) return;
        fontManagementState.quarantineTotal = Number(page.totalCount || 0);
        renderPageStatus('#QuarantinePageStatus', page.page || fontManagementState.quarantinePage, fontManagementState.quarantineTotal);
        document.querySelector('#QuarantinePreviousPage').disabled = fontManagementState.quarantinePage <= 1;
        document.querySelector('#QuarantineNextPage').disabled = fontManagementState.quarantinePage * Math.min(200, fontManagementState.pageSize) >= fontManagementState.quarantineTotal;
        renderQuarantineItems(page.items || []);
    }).catch(function (error) {
        if (loadId !== fontManagementState.quarantineLoadId) return;
        setViewState('#FontQuarantineState', 'error', formatText(strings.errorState, error.message));
    });
}

function namingErrorText(error) {
    var strings = currentStrings();
    var text = String(error || '');
    if (strings.namingErrors[text]) return strings.namingErrors[text];
    if (strings.renameErrors[text]) return strings.renameErrors[text];
    Object.keys(strings.namingErrors).forEach(function (code) {
        text = text.split(code).join(strings.namingErrors[code]);
    });
    return text;
}

function readNamingRules() {
    return {
        fileNameTemplate: document.querySelector('#txtFontFileNameTemplate').value,
        folderTemplate: document.querySelector('#txtFontFolderTemplate').value,
        removeEmptyDirectories: !!document.querySelector('#chkRemoveEmptyFontDirectories').checked,
        formatVersion: 2
    };
}

function validateNamingRules() {
    return postFontJson('rename/templates/validate', readNamingRules()).then(function (result) {
        fontManagementState.namingDefault = result.defaultFileNameTemplate;
        fontManagementState.namingFields = result.fields || [];
        renderNamingHelp();
        var errors = result.errors || [];
        setElementText('#FontNamingValidation', errors.map(function (error) {
            return formatText(currentStrings().ruleErrorPosition, Number(error.offset) + 1, namingErrorText(error.code));
        }).join('\n'));
        if (errors.length) {
            var first = errors[0];
            var input = document.querySelector(first.field === 'FontFolderTemplate' ? '#txtFontFolderTemplate' : '#txtFontFileNameTemplate');
            if (input.focus) input.focus();
            if (input.setSelectionRange) input.setSelectionRange(first.offset, first.offset + 1);
        }
        return errors.length === 0;
    });
}

function renderNamingHelp() {
    var strings = currentStrings();
    var content = document.querySelector('#FontNamingHelpContent');
    clearElement(content);
    fontManagementState.namingFields.forEach(function (field) {
        var description = strings.fieldDescriptions[field.name] || field.name;
        var policy = strings.missingPolicies[field.missingBehavior] || field.missingBehavior;
        appendDetail(content, '{' + field.name + '}', description + '\n' + policy);
    });
    appendDetail(content, strings.namingDefaultLabel, fontManagementState.namingDefault);
    appendDetail(content, strings.namingHelp, strings.namingExamples);
    appendDetail(content, strings.namingFieldUsage, strings.namingFieldUsageHelp);
    appendDetail(content, strings.namingFormatting, strings.namingSyntax);
    appendDetail(content, strings.namingCollisionRule, strings.namingCollisionHelp);
}


function renderRenameRules() {
    var strings = currentStrings();
    var context = fontManagementState.renameContext;
    var rules = context && context.rules;
    var box = document.querySelector('#RenameRulesValues');
    clearElement(box);
    if (rules) {
        appendDetail(box, strings.fileNameRule, rules.fileNameTemplate);
        appendDetail(box, strings.folderRule, rules.folderTemplate || strings.originalDirectory);
        appendDetail(box, strings.removeEmptyDirs, rules.removeEmptyDirectories ? strings.yesValue : strings.noValue);
        appendDetail(box, strings.namingCollisionRule, strings.namingCollisionHelp);
        appendDetail(box, strings.overviewGeneration, context.generationId);
        appendDetail(box, strings.batchColumn, context.analysisBatchId);
    }
    document.querySelector('#NormalizeSelectedButton').disabled = !context || !fontManagementState.renameCurrent || fontManagementState.renameStatus !== 'pending';
    document.querySelector('#RollbackSelectedButton').disabled = renameCandidatesBusy() || fontManagementState.renameStatus !== 'renamed';
}

function appendRenamePath(parent, label, value) {
    var part = document.createElement('div');
    part.className = 'fontPathBlock';
    var title = document.createElement('span');
    title.className = 'fontPathLabel';
    title.textContent = label;
    var body = document.createElement('span');
    body.className = 'fontPathFull';
    body.textContent = value || currentStrings().notAvailable;
    part.appendChild(title);
    part.appendChild(body);
    parent.appendChild(part);
}

function renderNamingEvidence(parent, item) {
    var strings = currentStrings();
    appendDetail(parent, 'SHA-256', item.sha256);
    appendDetail(parent, strings.sourceRoot, item.sourceRoot);
    var evidence = item.namingEvidence || { fields: [], selections: [] };
    var fields = new Map();
    (evidence.fields || []).forEach(function (field) {
        if (!fields.has(field.name)) fields.set(field.name, new Map());
        var key = JSON.stringify([field.value == null ? null : field.value, field.source || '', field.nameId == null ? null : field.nameId, field.faceIndex == null ? null : field.faceIndex, field.missingReason || null]);
        fields.get(field.name).set(key, field);
    });
    fields.forEach(function (observations, name) {
        var values = Array.from(observations.values()).map(function (field) {
            var source = (strings.evidenceSources[field.source] || field.source || '')
                + (field.nameId == null ? '' : ' · Name ID ' + field.nameId)
                + (field.faceIndex == null ? '' : ' · FaceIndex ' + field.faceIndex);
            var value = field.value == null ? strings.notAvailable : field.value === '' ? strings.namingEmptyValue : field.value;
            var reason = field.missingReason ? namingErrorText(field.missingReason)
                : field.value === '' ? strings.namingIntentionalEmpty : '';
            return value + '\n' + source + (reason ? '\n' + reason : '');
        });
        appendDetail(parent, '{' + name + '}', values.join('\n\n'));
    });
    var selections = new Map();
    (evidence.selections || []).forEach(function (selection) {
        selections.set(JSON.stringify([selection.faceIndex, selection.kind, selection.detail]), selection);
    });
    if (selections.size) {
        appendDetail(parent, strings.namingSelections, Array.from(selections.values()).map(function (selection) {
            return (strings.selectionKinds[selection.kind] || selection.kind)
                + (selection.faceIndex == null ? '' : ' · FaceIndex ' + selection.faceIndex)
                + (selection.detail ? '\n' + namingErrorText(selection.detail) : '');
        }).join('\n\n'));
    }
    var versions = Array.from((fields.get('version') || new Map()).values());
    var version = versions.find(function (field) { return field.faceIndex == null; }) || versions[0];
    appendDetail(parent, strings.namingVersionAvailable, version
        ? version.missingReason ? namingErrorText(version.missingReason) : version.value || strings.notAvailable
        : strings.notAvailable);
    appendDetail(parent, strings.collisionFingerprint, item.collisionFingerprint || strings.namingFingerprintUnused);
}


function loadRenameDirectories(batchId, rows) {
    return getFontJson('rename/batches/' + batchId + '/directories').then(function (records) {
        clearElement(rows);
        (records || []).forEach(function (record) {
            var card = document.createElement('div');
            card.className = 'fontRenameCard';
            appendRenamePath(card, currentStrings().directoryResults, record.path);
            appendDetail(card, currentStrings().statusColumn, localizedEnumName(record.status, ['Planned', 'Completed', 'Retained', 'Warning'], currentStrings().directoryStatuses));
            if (record.error) appendDetail(card, currentStrings().errorColumn, namingErrorText(record.error));
            rows.appendChild(card);
        });
    }).catch(function (error) { rows.textContent = error.message; });
}

function renderRenameItems(items) {
    var strings = currentStrings();
    var list = document.querySelector('#FontRenameRows');
    clearElement(list);
    (items || []).forEach(function (row) {
        var item = row.item;
        var card = document.createElement('article');
        card.className = 'fontRenameCard';
        var header = document.createElement('div');
        header.className = 'fontCardHeader';
        function canSelect() {
            return !renameCandidatesBusy() && (row.canExecute && fontManagementState.renameCurrent || row.canRollback);
        }
        function updateSelection() {
            var selectable = canSelect();
            if (!selectable) fontManagementState.renameFileIds.delete(row.key);
            var selected = fontManagementState.renameFileIds.has(row.key);
            card.className = 'fontRenameCard' + (selectable ? ' fontRenameCard--selectable' : '') + (selected ? ' fontRenameCard--selected' : '');
            card.setAttribute('role', 'button');
            card.setAttribute('aria-label', strings.selectColumn + ' ' + formatText(strings.fileNumber, item.fileId));
            card.setAttribute('aria-pressed', String(selected));
            card.setAttribute('aria-disabled', String(!selectable));
            card.tabIndex = selectable ? 0 : -1;
        }
        function toggleSelection() {
            if (!canSelect()) return;
            if (fontManagementState.renameFileIds.has(row.key)) fontManagementState.renameFileIds.delete(row.key);
            else fontManagementState.renameFileIds.add(row.key);
            updateSelection();
        }
        card.addEventListener('click', function (event) {
            if (event.target.closest('summary, button, a, input, select, textarea')) return;
            toggleSelection();
        });
        card.addEventListener('keydown', function (event) {
            if (event.target !== card || (event.key !== 'Enter' && event.key !== ' ') || !canSelect()) return;
            event.preventDefault();
            toggleSelection();
        });
        updateSelection();
        var title = document.createElement('h3');
        title.className = 'fontCardTitle';
        title.textContent = formatText(strings.fileNumber, item.fileId);
        header.appendChild(title);
        appendBadge(header, localizedEnumName(item.status, ['Pending', 'Moving', 'Temporary', 'Renamed', 'RollingBack', 'RolledBack', 'DestinationOccupied', 'EvidenceDrift', 'InUse', 'Skipped', 'FailedIo'], strings.renameStatuses), !!item.error);
        card.appendChild(header);
        var paths = document.createElement('div');
        paths.className = 'fontPathGrid';
        var oldParts = String(item.oldPath || '').split(/[\\/]/);
        var newParts = String(item.newPath || '').split(/[\\/]/);
        appendRenamePath(paths, strings.originalDirectory, oldParts.slice(0, -1).join('/') || item.sourceRoot);
        appendRenamePath(paths, strings.originalFilename, oldParts[oldParts.length - 1]);
        appendRenamePath(paths, strings.targetDirectory, newParts.slice(0, -1).join('/'));
        appendRenamePath(paths, strings.targetFilename, newParts[newParts.length - 1]);
        card.appendChild(paths);
        if (item.error) {
            var error = document.createElement('p');
            error.className = 'fontDiagnostic';
            var skipped = enumName(item.status, ['Pending', 'Moving', 'Temporary', 'Renamed', 'RollingBack', 'RolledBack', 'DestinationOccupied', 'EvidenceDrift', 'InUse', 'Skipped', 'FailedIo']) === 'Skipped';
            var reason = item.error === 'unclassifiable-or-link' && item.inspectionError
                ? strings.renameUnclassifiableReason + '：' + formatDiagnostic(item.inspectionError)
                : namingErrorText(item.error);
            error.textContent = (skipped ? strings.renameSkipReason : strings.errorColumn) + '：' + reason;
            card.appendChild(error);
        }
        var details = document.createElement('details');
        details.className = 'fontDisclosure';
        var summary = document.createElement('summary');
        summary.textContent = strings.showNamingDetails;
        details.appendChild(summary);
        var evidence = document.createElement('div');
        evidence.className = 'fontDetailContent fontDetailGrid';
        renderNamingEvidence(evidence, item);
        appendDetail(evidence, strings.overviewGeneration, row.generationId);
        if (row.maintenanceBatchId) appendDetail(evidence, strings.batchColumn, row.maintenanceBatchId);
        details.appendChild(evidence);
        card.appendChild(details);
        if (row.maintenanceBatchId) {
            var directories = document.createElement('details');
            var title = document.createElement('summary');
            title.textContent = strings.directoryResults;
            directories.appendChild(title);
            var results = document.createElement('div');
            directories.appendChild(results);
            var loaded = false;
            directories.addEventListener('toggle', function () {
                if (directories.open && !loaded) { loaded = true; loadRenameDirectories(row.maintenanceBatchId, results); }
            });
            card.appendChild(directories);
        }
        list.appendChild(card);
    });
    renderPageStatus('#RenameItemsPageStatus' , fontManagementState.renameItemPage, fontManagementState.renameItemTotal);
    document.querySelector('#RenameItemsPreviousPage').disabled = fontManagementState.renameItemPage <= 1;
    document.querySelector('#RenameItemsNextPage').disabled = fontManagementState.renameItemPage * Math.min(200, fontManagementState.pageSize) >= fontManagementState.renameItemTotal;
    setViewState('#FontRenameState', fontManagementState.renameItemTotal ? 'ready' : 'empty', fontManagementState.renameItemTotal ? '' : strings.emptyState);
}

function renameCandidatesBusy() {
    var state = fontManagementState.renameCandidateState;
    return !!state && (state.status === 'pending' || state.status === 'running');
}

function isRenamePageVisible() {
    return !fontManagementState.pageHidden && fontManagementState.activeView === 'rename'
        && document.querySelector('#FontManagementPanel').hidden !== true;
}

function stopRenamePolling() {
    if (fontManagementState.renamePollTimer !== null) window.clearTimeout(fontManagementState.renamePollTimer);
    fontManagementState.renamePollTimer = null;
}

function cancelRenamePageRead() {
    stopRenamePolling();
    fontManagementState.renameLoadId++;
    if (fontManagementState.renameReadAbort) fontManagementState.renameReadAbort.abort();
    fontManagementState.renameReadAbort = null;
    fontManagementState.renameContext = null;
    fontManagementState.renameCurrent = false;
    fontManagementState.renameFileIds.clear();
    document.querySelector('#NormalizeSelectedButton').disabled = true;
}

function renderRenameCandidateState(candidateState, blockReason) {
    var strings = currentStrings();
    var status = candidateState && candidateState.status;
    var labels = {
        pending: strings.renameCandidatesPending, running: strings.renameCandidatesRunning,
        ready: strings.renameCandidatesReady, stale: strings.renameCandidatesStale, failed: strings.renameCandidatesFailed
    };
    var message = labels[status] || (fontManagementState.renameCanRefresh ? strings.renameCandidatesPending : strings.renameCandidatesNeedAnalysis);
    if (candidateState && candidateState.generatedUtc) message += ' · ' + formatText(strings.renameCandidatesGenerated, formatDate(candidateState.generatedUtc));
    if (candidateState && candidateState.error) message += ' · ' + candidateState.error;
    else if (blockReason && status !== 'pending' && status !== 'running') message += ' · ' + blockReason;
    setElementText('#RenameContextValidity', message);
    document.querySelector('#RefreshRenameCandidatesButton').disabled = !fontManagementState.renameCanRefresh
        || fontManagementState.renameRefreshInFlight || renameCandidatesBusy();
}

function scheduleRenameCandidatePoll() {
    stopRenamePolling();
    if (!isRenamePageVisible() || !fontManagementState.renameCanRefresh) return;
    var state = fontManagementState.renameCandidateState;
    var stale = !state || state.status === 'stale';
    if (stale && !fontManagementState.renameStalePollUntil) fontManagementState.renameStalePollUntil = Date.now() + 15000;
    if (!stale) fontManagementState.renameStalePollUntil = 0;
    if (!renameCandidatesBusy() && !(stale && Date.now() < fontManagementState.renameStalePollUntil)) return;
    fontManagementState.renamePollTimer = window.setTimeout(function () {
        fontManagementState.renamePollTimer = null;
        if (isRenamePageVisible()) return loadFontRename();
    }, 1500);
}

function queueRenameCandidateRefresh() {
    if (fontManagementState.renameRefreshInFlight) return Promise.resolve(null);
    cancelRenamePageRead();
    fontManagementState.renameRefreshInFlight = true;
    fontManagementState.renameRefreshKey = fontManagementState.renameRefreshKey || createIdempotencyKey();
    renderRenameRules();
    renderRenameItems(fontManagementState.renameRows);
    renderRenameCandidateState(fontManagementState.renameCandidateState, null);
    return postFontJson('operations/refresh-rename-candidates', { idempotencyKey: fontManagementState.renameRefreshKey }).then(function (operation) {
        fontManagementState.renameRefreshKey = null;
        fontManagementState.renameRefreshInFlight = false;
        showOperation(operation);
        fontManagementState.renameCandidateState = { status: 'pending', operationRequestId: operation.id };
        renderRenameRules();
        renderRenameItems(fontManagementState.renameRows);
        renderRenameCandidateState(fontManagementState.renameCandidateState, null);
        if (isRenamePageVisible()) return loadFontRename();
        return operation;
    }).catch(function (error) {
        fontManagementState.renameRefreshInFlight = false;
        renderRenameCandidateState(fontManagementState.renameCandidateState, null);
        setElementText('#FontOperationStatus', formatText(currentStrings().errorState, error.message));
        return null;
    });
}

function loadFontRename() {
    stopRenamePolling();
    if (fontManagementState.renameReadAbort) fontManagementState.renameReadAbort.abort();
    var controller = new AbortController();
    fontManagementState.renameReadAbort = controller;
    var loadId = ++fontManagementState.renameLoadId;
    var status = fontManagementState.renameStatus;
    var page = fontManagementState.renameItemPage;
    fontManagementState.renameFileIds.clear();
    document.querySelector('#NormalizeSelectedButton').disabled = true;
    setViewState('#FontRenameState', 'loading', currentStrings().loadingState);
    return authenticatedFetch('rename/items', { method: 'GET', signal: controller.signal },
        { status: status, page: page, pageSize: Math.min(200, fontManagementState.pageSize) }).then(readResponseJson).then(function (result) {
        if (loadId !== fontManagementState.renameLoadId || status !== fontManagementState.renameStatus || page !== fontManagementState.renameItemPage) return;
        fontManagementState.renameCandidateState = result.candidateState || null;
        fontManagementState.renameCanRefresh = result.canRefreshCandidates !== false;
        fontManagementState.renameContext = result.current || null;
        fontManagementState.renameCurrent = !!result.current && (!result.candidateState || result.candidateState.status === 'ready');
        fontManagementState.renameRows = result.items || [];
        fontManagementState.renameItems = fontManagementState.renameRows;
        fontManagementState.renameItemTotal = Number(result.totalCount || 0);
        renderRenameRules();
        renderRenameItems(fontManagementState.renameRows);
        renderRenameCandidateState(fontManagementState.renameCandidateState, result.blockReason);
        scheduleRenameCandidatePoll();
    }).catch(function (error) {
        if (loadId !== fontManagementState.renameLoadId || error.name === 'AbortError') return;
        fontManagementState.renameContext = null;
        fontManagementState.renameCurrent = false;
        fontManagementState.renameRows = [];
        fontManagementState.renameFileIds.clear();
        renderRenameRules();
        setViewState('#FontRenameState', 'error', formatText(currentStrings().errorState, error.message));
    }).finally(function () {
        if (loadId === fontManagementState.renameLoadId) fontManagementState.renameReadAbort = null;
    });
}

function renderDatabase(health, samples) {
    var strings = currentStrings();
    var values = document.querySelector('#FontDatabaseValues');
    var body = document.querySelector('#FontPerformanceRows');
    clearElement(values);
    clearElement(body);
    appendDefinition(values, strings.databaseSchema, health.schemaVersion == null ? strings.notAvailable : health.schemaVersion);
    appendDefinition(values, strings.databaseJournal, health.journalMode || strings.notAvailable);
    appendDefinition(values, strings.databaseSize, health.size == null ? strings.notAvailable : formatBytes(health.size));
    appendDefinition(values, strings.databaseRecovery, health.recoveredCorruptDatabasePath || strings.notAvailable, !!health.recoveredCorruptDatabasePath);
    appendDefinition(values, strings.databaseChecked, formatDate(health.checkedUtc));
    (samples || []).forEach(function (sample) {
        var row = document.createElement('tr');
        createTextCell(row, phaseName(sample.phase));
        createTextCell(row, formatDuration(sample.duration));
        createTextCell(row, formatNumber(sample.itemCount));
        createTextCell(row, formatNumber(sample.reusedCount));
        createTextCell(row, formatDate(sample.recordedUtc));
        body.appendChild(row);
    });
    if (!samples || !samples.length) {
        var emptyRow = document.createElement('tr');
        var emptyCell = createTextCell(emptyRow, strings.emptyState);
        emptyCell.colSpan = 5;
        body.appendChild(emptyRow);
    }
}

function loadActiveFontView() {
    switch (fontManagementState.activeView) {
        case 'analysis': return loadFontAnalysis();
        case 'quarantine': return loadFontQuarantine();
        case 'rename': return loadFontRename();
        default: return loadFontOverview();
    }
}

function showFontView(viewName) {
    if (viewName !== 'rename') cancelRenamePageRead();
    fontManagementState.activeView = viewName;
    document.querySelectorAll('[data-font-view]').forEach(function (view) {
        view.hidden = view.getAttribute('data-font-view') !== viewName;
    });
    document.querySelectorAll('[data-font-tab]').forEach(function (button) {
        button.setAttribute('aria-selected', button.getAttribute('data-font-tab') === viewName ? 'true' : 'false');
    });
    loadActiveFontView();
}

function showMainTab(tabName) {
    var settings = tabName === 'settings';
    if (settings) cancelRenamePageRead();
    document.querySelector('#GeneralSettingsPanel').hidden = !settings;
    document.querySelector('#FontManagementPanel').hidden = settings;
    document.querySelector('#GeneralSettingsTab').setAttribute('aria-selected', settings ? 'true' : 'false');
    document.querySelector('#FontManagementTab').setAttribute('aria-selected', settings ? 'false' : 'true');
    if (!settings) loadActiveFontView();
}

function applyLocalization(locale) {
    var lang = (locale && locale.toLowerCase().startsWith('zh')) ? 'zh' : 'en';
    fontManagementState.locale = lang;
    var strings = i18nStrings[lang];
    if (!strings) return;

    document.querySelectorAll('[data-i18n]').forEach(function(el) {
        var key = el.getAttribute('data-i18n');
        if (strings[key]) {
            el.textContent = strings[key];
        }
    });
    document.querySelectorAll('#ReportRelationshipFilter option').forEach(function (option) {
        if (option.value) option.textContent = strings.relationshipLabels[option.value] || option.textContent;
    });
    document.querySelectorAll('#ReportActionFilter option').forEach(function (option) {
        if (option.value) option.textContent = strings.actionLabels[option.value] || option.textContent;
    });
}

function updateModeVisibility() {
    var mode = document.querySelector('#SubtitleMode').value;
    var supOpts = document.querySelector('#SupOptions');
    if (mode === '1') {
        supOpts.style.display = '';
    } else {
        supOpts.style.display = 'none';
    }
}

document.querySelector('#SubtitleMode').addEventListener('change', updateModeVisibility);

document.querySelectorAll('[data-main-tab]').forEach(function (button) {
    button.addEventListener('click', function () {
        showMainTab(button.getAttribute('data-main-tab'));
    });
});

document.querySelectorAll('[data-font-tab]').forEach(function (button) {
    button.addEventListener('click', function () {
        showFontView(button.getAttribute('data-font-tab'));
    });
});

document.querySelector('#AnalysisBatchSelect').addEventListener('change', function (event) {
    var option = event.target.options[event.target.selectedIndex];
    fontManagementState.analysisBatchId = Number(event.target.value) || null;
    fontManagementState.analysisBatch = option && option.dataset.batch ? JSON.parse(option.dataset.batch) : null;
    fontManagementState.reportPage = 1;
    fontManagementState.selectedFileIds.clear();
    updateReportSelection();
    loadAnalysisReports();
});

document.querySelector('#ReportApplyFilters').addEventListener('click', function () {
    fontManagementState.reportPage = 1;
    loadAnalysisReports();
});

bindPageSelect('#ReportPageSelect', 'reportPage', 'reportTotal', loadAnalysisReports);
bindPageSelect('#QuarantinePageSelect', 'quarantinePage', 'quarantineTotal', loadFontQuarantine);
bindPageSelect('#RenameItemsPageSelect', 'renameItemPage', 'renameItemTotal', loadFontRename);

document.querySelector('#ReportPreviousPage').addEventListener('click', function () {
    if (fontManagementState.reportPage > 1) {
        fontManagementState.reportPage--;
        loadAnalysisReports();
    }
});

document.querySelector('#ReportNextPage').addEventListener('click', function () {
    if (fontManagementState.reportPage * Math.min(200, fontManagementState.pageSize) < fontManagementState.reportTotal) {
        fontManagementState.reportPage++;
        loadAnalysisReports();
    }
});

document.querySelector('#RefreshQuarantineButton').addEventListener('click', loadFontQuarantine);

['quarantined', 'pending', 'failed'].forEach(function (status) {
    var id = '#Quarantine' + status.charAt(0).toUpperCase() + status.slice(1) + 'Tab';
    document.querySelector(id).addEventListener('click', function () {
        fontManagementState.quarantineStatus = status;
        fontManagementState.quarantinePage = 1;
        loadFontQuarantine();
    });
});

document.querySelector('#QuarantinePreviousPage').addEventListener('click', function () {
    if (fontManagementState.quarantinePage > 1) {
        fontManagementState.quarantinePage--;
        loadFontQuarantine();
    }
});

document.querySelector('#QuarantineNextPage').addEventListener('click', function () {
    if (fontManagementState.quarantinePage * Math.min(200, fontManagementState.pageSize) < fontManagementState.quarantineTotal) {
        fontManagementState.quarantinePage++;
        loadFontQuarantine();
    }
});

['pending', 'skipped', 'failed', 'renamed', 'rolledback'].forEach(function (status) {
    document.querySelector('#RenameStatus-' + status).addEventListener('click', function () {
        fontManagementState.renameStatus = status;
        fontManagementState.renameItemPage = 1;
        fontManagementState.renameFileIds.clear();
        ['pending', 'skipped', 'failed', 'renamed', 'rolledback'].forEach(function (view) {
            document.querySelector('#RenameStatus-' + view).setAttribute('aria-selected', String(view === status));
        });
        return loadFontRename();
    });
});

document.querySelector('#RenameItemsPreviousPage').addEventListener('click', function () {
    if (fontManagementState.renameItemPage > 1) { fontManagementState.renameItemPage--; return loadFontRename(); }
});

document.querySelector('#RenameItemsNextPage').addEventListener('click', function () {
    if (fontManagementState.renameItemPage * Math.min(200, fontManagementState.pageSize) < fontManagementState.renameItemTotal) { fontManagementState.renameItemPage++; return loadFontRename(); }
});

document.querySelector('#AnalyzeFontsButton').addEventListener('click', function () {
    queueOperation('operations/analyze', { idempotencyKey: createIdempotencyKey() }).then(loadFontOverview);
});

document.querySelector('#ExportAnalysisButton').addEventListener('click', function () {
    var batchId = fontManagementState.analysisBatchId;
    if (!batchId) {
        setElementText('#FontOperationStatus', currentStrings().emptyState);
        return;
    }
    authenticatedFetch('export', { method: 'GET' }, { analysisBatchId: batchId }).then(function (response) {
        if (!response.ok) throw new Error(response.statusText || String(response.status));
        return response.blob();
    }).then(function (blob) {
        var url = URL.createObjectURL(blob);
        var link = document.createElement('a');
        link.href = url;
        link.download = 'font-analysis-' + batchId + '.json';
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(url);
        setElementText('#FontOperationStatus', formatText(currentStrings().exportComplete, batchId));
    }).catch(function (error) {
        setElementText('#FontOperationStatus', formatText(currentStrings().errorState, error.message));
    });
});

document.querySelector('#ClearReportSelectionButton').addEventListener('click', function () {
    fontManagementState.selectedFileIds.clear();
    updateReportSelection();
});

document.querySelector('#QuarantineSelectedButton').addEventListener('click', function () {
    var selected = Array.from(fontManagementState.selectedFileIds).sort(function (left, right) { return left - right; });
    if (!fontManagementState.analysisBatchId || !selected.length || !fontManagementState.analysisBatch || fontManagementState.analysisBatch.isCurrent !== true) {
        setElementText('#FontOperationStatus', currentStrings().selectRequired);
        return;
    }
    queueOperation('operations/quarantine', {
        batchId: fontManagementState.analysisBatchId,
        selectedFileIds: selected,
        idempotencyKey: createIdempotencyKey()
    }).then(loadFontAnalysis);
});

document.querySelector('#QuarantineAllVerifiedButton').addEventListener('click', confirmAllVerified);

document.querySelector('#RestoreSelectedButton').addEventListener('click', function () {
    var selected = Array.from(fontManagementState.quarantineSelections.values());
    if (!selected.length) {
        setElementText('#FontOperationStatus', currentStrings().selectRequired);
        return;
    }
    var byBatch = new Map();
    selected.forEach(function (item) {
        if (!byBatch.has(item.batchId)) byBatch.set(item.batchId, []);
        byBatch.get(item.batchId).push(item.fileId);
    });
    var batches = Array.from(byBatch.keys()).sort(function (left, right) { return left - right; });
    batches.reduce(function (pending, batchId) {
        return pending.then(function (previous) {
            if (previous === null) return null;
            var fileIds = byBatch.get(batchId).sort(function (left, right) { return left - right; });
            return queueOperation('operations/restore', {
                batchId: batchId,
                selectedFileIds: fileIds,
                idempotencyKey: createIdempotencyKey()
            });
        });
    }, Promise.resolve(true)).then(loadFontQuarantine);
});

document.querySelector('#PurgeExpiredButton').addEventListener('click', function () {
    if (!window.confirm(currentStrings().confirmPurge)) return;
    queueOperation('operations/purge', { idempotencyKey: createIdempotencyKey() }).then(loadFontQuarantine);
});

document.querySelector('#NormalizeSelectedButton').addEventListener('click', function () {
    var context = fontManagementState.renameContext;
    var selected = fontManagementState.renameRows.filter(function (row) { return row.canExecute && fontManagementState.renameFileIds.has(row.key); }).map(function (row) { return row.fileId; }).sort(function (left, right) { return left - right; });
    if (!context || !selected.length) { setElementText('#FontOperationStatus', currentStrings().selectRequired); return; }
    return queueOperation('operations/normalize-names', {
        generationId: context.generationId,
        analysisBatchId: context.analysisBatchId,
        ruleFingerprint: context.ruleFingerprint,
        selectedFileIds: selected,
        idempotencyKey: createIdempotencyKey()
    }).then(loadFontRename);
});

document.querySelector('#RollbackSelectedButton').addEventListener('click', function () {
    var byBatch = new Map();
    fontManagementState.renameRows.filter(function (row) { return row.canRollback && fontManagementState.renameFileIds.has(row.key); }).forEach(function (row) {
        if (!byBatch.has(row.maintenanceBatchId)) byBatch.set(row.maintenanceBatchId, []);
        byBatch.get(row.maintenanceBatchId).push(row.fileId);
    });
    if (!byBatch.size) { setElementText('#FontOperationStatus', currentStrings().selectRequired); return; }
    return Array.from(byBatch.keys()).sort(function (left, right) { return left - right; }).reduce(function (pending, batchId) {
        return pending.then(function (previous) {
            if (previous === null) return null;
            var selected = byBatch.get(batchId).sort(function (left, right) { return left - right; });
            return queueOperation('operations/rename-rollback', {
                batchId: batchId,
                selectedFileIds: selected,
                idempotencyKey: createIdempotencyKey()
            });
        });
    }, Promise.resolve(true)).then(loadFontRename);
});

document.querySelector('#RefreshRenameCandidatesButton').addEventListener('click', queueRenameCandidateRefresh);

document.querySelector('#ResetFontNameTemplateButton').addEventListener('click', function () {
    return validateNamingRules().then(function () {
        document.querySelector('#txtFontFileNameTemplate').value = fontManagementState.namingDefault;
        return validateNamingRules();
    }).catch(function (error) { setElementText('#FontNamingValidation', error.message); });
});

document.querySelector('#CopyDefaultFontNameTemplateButton').addEventListener('click', function () {
    return validateNamingRules().then(function () {
        if (window.navigator && window.navigator.clipboard) return window.navigator.clipboard.writeText(fontManagementState.namingDefault);
        document.querySelector('#DefaultFontNameTemplate').value = fontManagementState.namingDefault;
        document.querySelector('#DefaultFontNameTemplate').hidden = false;
        if (document.querySelector('#DefaultFontNameTemplate').select) document.querySelector('#DefaultFontNameTemplate').select();
    }).catch(function (error) { setElementText('#FontNamingValidation', error.message); });
});

document.querySelector('#RebuildDerivedDataButton').addEventListener('click', function () {
    if (!window.confirm(currentStrings().confirmRebuild)) return;
    return queueOperation('operations/rebuild-derived-data', { idempotencyKey: createIdempotencyKey() }).then(loadFontOverview);
});

document.querySelector('#AssSubsetterConfigPage').addEventListener('pagehide', function () {
    fontManagementState.pageHidden = true;
    cancelRenamePageRead();
});

document.querySelector('#AssSubsetterConfigPage').addEventListener('pageshow', function () {
    fontManagementState.pageHidden = false;
    showMainTab('settings');
    Dashboard.showLoadingMsg();
    return Promise.all([
        ApiClient.getPluginConfiguration(AssSubsetterConfig.pluginUniqueId),
        ApiClient.getServerConfiguration()
    ]).then(function (results) {
        var config = results[0];
        var serverConfig = results[1];

        applyLocalization(serverConfig.UICulture);
        renderNamingHelp();
        document.querySelector('#txtFontFileNameTemplate').value = config.FontFileNameTemplate || '';
        document.querySelector('#txtFontFolderTemplate').value = config.FontFolderTemplate || '';
        document.querySelector('#chkRemoveEmptyFontDirectories').checked = config.RemoveEmptyFontDirectories === true;

        var modeVal = config.SubtitleMode;
        if (modeVal === 'Subsetting' || modeVal === 0) modeVal = '0';
        if (modeVal === 'ConvertToSup' || modeVal === 1) modeVal = '1';
        if (modeVal === 'GenerateMks' || modeVal === 2) modeVal = '2';

        document.querySelector('#SubtitleMode').value = modeVal || '0';
        document.querySelector('#CustomFontDirectories').value = config.CustomFontDirectories || "";
        document.querySelector('#ProtectedFontDirectories').value = config.ProtectedFontDirectories || "";
        document.querySelector('#PreferredFontDirectories').value = config.PreferredFontDirectories || "";
        document.querySelector('#MaxCacheSizeMB').value = config.MaxCacheSizeMB || 1024;
        document.querySelector('#FontHistoryRetentionCount').value = config.FontHistoryRetentionCount ?? 3;
        document.querySelector('#EnableAutoScanProcessing').checked = config.EnableAutoScanProcessing !== false;
        document.querySelector('#EnablePrefetchSubsetting').checked = config.EnablePrefetchSubsetting !== false;
        document.querySelector('#PrefetchTriggerPercent').value = config.PrefetchTriggerPercent || 85;
        document.querySelector('#FallbackToOriginalOnError').checked = config.FallbackToOriginalOnError !== false;
        document.querySelector('#AssToSupFrameRate').value = config.AssToSupFrameRate || 24;
        updateModeVisibility();
        return validateNamingRules();
    }).catch(function (error) {
        setElementText('#FontNamingValidation', error.message);
    }).finally(function () { Dashboard.hideLoadingMsg(); });
});

document.querySelector('#AssSubsetterConfigForm').addEventListener('submit', function (e) {
    e.preventDefault();
    Dashboard.showLoadingMsg();

    return validateNamingRules().then(function (valid) {
        if (!valid) return null;
        var rules = readNamingRules();
        return ApiClient.getPluginConfiguration(AssSubsetterConfig.pluginUniqueId).then(function (config) {
            config.FontFileNameTemplate = rules.fileNameTemplate;
            config.FontFolderTemplate = rules.folderTemplate;
            config.RemoveEmptyFontDirectories = rules.removeEmptyDirectories;
            config.SubtitleMode = parseInt(document.querySelector('#SubtitleMode').value || "0", 10);
            config.CustomFontDirectories = document.querySelector('#CustomFontDirectories').value;
            config.ProtectedFontDirectories = document.querySelector('#ProtectedFontDirectories').value;
            config.PreferredFontDirectories = document.querySelector('#PreferredFontDirectories').value;
            config.MaxCacheSizeMB = parseInt(document.querySelector('#MaxCacheSizeMB').value || "1024", 10);
            config.FontHistoryRetentionCount = parseInt(document.querySelector('#FontHistoryRetentionCount').value || "1", 10);
            config.EnableAutoScanProcessing = document.querySelector('#EnableAutoScanProcessing').checked;
            config.EnablePrefetchSubsetting = document.querySelector('#EnablePrefetchSubsetting').checked;
            config.PrefetchTriggerPercent = parseInt(document.querySelector('#PrefetchTriggerPercent').value || "90", 10);
            config.FallbackToOriginalOnError = document.querySelector('#FallbackToOriginalOnError').checked;
            config.AssToSupFrameRate = parseInt(document.querySelector('#AssToSupFrameRate').value || "24", 10);

            return ApiClient.updatePluginConfiguration(AssSubsetterConfig.pluginUniqueId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
                setElementText('#FontNamingValidation', currentStrings().ruleSaved);
                cancelRenamePageRead();
                fontManagementState.renameStalePollUntil = Date.now() + 15000;
                fontManagementState.renameCandidateState = { status: 'stale' };
                fontManagementState.renameContext = null;
                fontManagementState.renameCurrent = false;
                fontManagementState.renameFileIds.clear();
                renderRenameRules();
                renderRenameItems(fontManagementState.renameItems);
                renderRenameCandidateState(fontManagementState.renameCandidateState, null);
                if (isRenamePageVisible()) loadFontRename();
            });
        });
    }).catch(function (error) {
        setElementText('#FontNamingValidation', error.message);
    }).finally(function () { Dashboard.hideLoadingMsg(); });
});
