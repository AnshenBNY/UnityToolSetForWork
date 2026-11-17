# 工具改造验证记录

## 2026-11-07

- Unity 2022.3.62f3c1，使用 `Tests~/Run.ps1 -FocusedTestFile CopyReferenceWarningRegression.cs -ExecuteMethod CopyReferenceWarningRegression.Run -ResultFile copy-reference-warning-results.txt` 创建独立工程：**6 项通过，0 失败**。
- 验证正常批量复制、导入后重新引用原贴图时保留整批结果及完成回调、粒子 Mesh 槽残留原模型时保留结果、排除资源不误报，以及出现警告后真正失败/取消仍回滚。
- 验证完成状态显示残留引用数量、警告包含引用方/原资源/对应副本路径、重复检查不重复记录、成功后整批恢复可用，源文件及 .meta 保持不变。
- 全部编辑器源码与回归测试程序集编译通过，`git diff --check` 通过。未重新执行用户的 7 个预制体复制任务，未做窗口布局人工验收。
- 专项结果：`C:/Users/Administrator/AppData/Local/Temp/ToolSetRegression-3d093f40c76948a4af883c02c6208d3a/copy-reference-warning-results.txt`。

## 2026-10-13

- Unity 2022.3.14f1，使用 `Tests~/Run.ps1` 创建独立工程，`DependencyRecordRegression`：**10 项通过，0 失败**。
- 验证修复后状态与统计刷新、重新解析同一 JSON、JSON 中断链中的引用文件、引用来源与候选移动、备份恢复及候选删除后的重新报错。
- 验证导入器 .meta 引用、引用换行/字段顺序/可选 type/大小写 GUID、普通 GUID 文本保持、同名不同扩展名候选筛选、检查结果变化后旧计划失效。
- 验证旧版已修复条目的实际依赖证据，无使用关系的同名候选、残留旧引用及无效 fileID 不会被标为已修复；无法识别的子资源继续阻止执行。
- 验证部分修复仍保留丢失状态、JSON 范围外根对象保持，以及共用修复模块的复制 GUID/fileID 与真正空引用。
- 全部编辑器源码与回归测试程序集编译通过，`git diff --check` 通过。完整回归未运行，当前工程缺少 `fish_200004005_skin.fbx` 夹具；本次未做 Unity 窗口人工验收。
- 专项结果：`C:/Users/iveco/AppData/Local/Temp/ToolSetRegression-4ed302084f8a4b739e92affbb44824f8/dependency-record-regression-results.txt`。

## 2026-09-15

- 完整复制的写入、导入或用户取消仍按事务整批恢复；写入完成后的旧资源引用复查和预制体复查改为保留副本、标记“需处理”并继续完成。
- 完成状态会明确显示保留结果及待处理问题数量，操作记录保留具体复查错误。
- `WorkflowPreviewRegression`：通过。覆盖复查步骤失败后计划成功完成、结果保留、问题进入结果列表，以及阻断问题仍禁止执行。
- 全部编辑器源码与完整回归测试程序集隔离编译通过；当前 Unity Console 无新增编译错误。

## 2026-09-14

- `SceneSaveRegression`：通过。场景实例不区分来源，修改直接保存到当前连接的预制体；输出目录可以为空，同一目标的多个实例会阻止冲突保存。
- 预制体复制页拆分为“完整复制 / 场景实例加工”两个工作区；两个模式的预览状态互相隔离，放入场景后自动进入实例加工。
- 全部编辑器源码与回归测试程序集隔离编译通过；`WorkflowPreviewRegression` 再次通过。完整回归未重跑，当前工程缺少 `fish_200004005_skin.fbx` 测试夹具。
- Unity 2022.3.14f1：当前工程脚本刷新后 Console 无编译错误。
- 全部编辑器源码与回归测试程序集隔离编译通过。
- `WorkflowPreviewRegression`：通过。覆盖页签计划隔离、普通问题继续执行、阻断问题禁止执行。
- `PrefabMappingNestedSourceRegression`：通过。覆盖最近嵌套预制体来源、同名不同来源冲突、源层级变化、重命名/贴图迁移/模型导入预览失效、未保存源资源阻断，以及候选搜索零结果状态。
- 当前工程 `TestScene` 有用户未保存修改，未直接运行 Unity Test Runner；隔离测试没有保存或改动该场景。

日期：2026-09-11

- Unity：2022.3.30f1c1，Windows，无图形批处理。
- 方式：运行 Tests~/Run.ps1，在 TEMP 下创建独立测试工程。
- 结果：**50 项检查通过，0 失败**。
- 本次复制清单验证：摘要按资源文件计数，与实际复制的目标路径集合一致；同一来源只出现一次；排除的预制体作为共享资源单独列出。
- 执行保护验证：准备清单不创建输出文件；修改设置后阻止旧清单执行；已完成的计划不会再次执行。
- 批量复制验证：根列表去重、共用资源只复制一次、同名根冲突避让、嵌套及变体指向副本、整批成功恢复/失败回滚/取消回滚、场景批量放入与一次 Undo、任一生成的根副本保存、列表偏好及旧设置迁移。
- 场景多选验证：六个复制模块共享副本、Timeline 各自绑定、嵌套筛选、输入失效、整批 Undo、失败及取消时还原原有引用和预制体覆盖、批量保存、同目标冲突拦截、保存失败保留场景修改。
- MeshFilter 回归覆盖真实 FBX 内仅由蒙皮渲染器使用的 Mesh，按子资源标识正确替换。
- 全部编辑器源码和测试程序集编译通过。
- 界面尚未在实际 Unity 窗口中进行视觉验收；本次没有性能基准测试。

## 原始结果

首行汇总 7 项粒子网格回归，其余每行各代表一项检查。

~~~text
PASS previous 7 particle mesh regressions
PASS Unique planned copy paths and folder boundaries
PASS Two same-name textures migrate independently and preserve material scale
PASS GUID text is untouched and object references can be truly cleared
PASS Missing FBX subasset repair is blocked without old identity
PASS Full dependency copy: real FBX multi-mesh, shape, skinned, raw image, nested exclusion and weights
PASS Save root copy preserves shared nested prefab and instance connection
PASS Rename collision preflight and file GUID preservation
PASS Hierarchy rename filters persistent assets and supports Undo
PASS Model importer rejects invalid errors and missing Avatar
PASS Renderer mapping supports roots and refuses ambiguous paths
PASS Backup refuses to overwrite later edits
PASS Workflow rolls back failed writes and releases update/progress state
PASS Model importer external material remap points to copied material
PASS Distinct same-name subassets map by identity and candidate ambiguity is rejected
PASS Copied references restore GUID plus fileID and original files remain untouched
PASS Nested prefab mapping keeps transforms and source connection; repeat skips duplicates
PASS Workflow cancellation restores created assets and folders, preview detects stale files
PASS Saving copied prefab does not save unrelated dirty materials
PASS Dependency report resolves moved root scopes by GUID
PASS Scene recovery failure does not prevent file rollback
PASS Manual recovery protects unsaved material edits
PASS Changing copy settings invalidates the prepared operation before writing
PASS Completed operations cannot be accidentally started twice
PASS Batch copy deduplicates shared FBX/materials, preserves nested and variant roots, and restores the whole batch
PASS Batch source validation blocks empty, missing, scene, model and excluded roots before writing
PASS Batch list changes and edits to the second source invalidate prepared copies
PASS Batch failure rolls back all roots and shared resources without publishing partial results
PASS Batch cancellation after copying both roots restores the entire transaction
PASS Batch results instantiate independently with one Undo and any generated root can be saved
PASS Batch input deduplication, typed preference round-trip, legacy migration and deliberate empty lists
PASS Scene batch selection filters assets/preview objects and collapses nested selections
PASS Scene batch module shares one resource set: CopyMaterialsFromMeshRender
PASS Scene batch module shares one resource set: CopyMaterialsFromParticle
PASS Scene batch module shares one resource set: CopyMeshFromParticle
PASS Scene batch module shares one resource set: CopyImageAndTextResources
PASS Scene batch module shares one resource set: CopyMeshFromMeshFilter
PASS Scene batch module shares one resource set: CopyAnimationAndTimeline
PASS Scene batch preparation detects changed lists, hierarchy and second-instance values
PASS Scene batch later failure restores references, overrides and all generated files
PASS Scene batch cancellation restores references and prefab overrides for the entire batch
PASS Successful scene batch reference edits support one Undo
PASS Scene batch saves multiple generated roots and rejects duplicate destination instances
PASS Scene batch save failure restores both files while retaining unsaved scene changes
Failures: 0
~~~

本次测试日志与临时工程：C:/Users/Administrator/AppData/Local/Temp/ToolSetRegression-c1685ca0fff742509ace5a503b03d954
