# 工具改造验证记录

## 2026-09-14

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
