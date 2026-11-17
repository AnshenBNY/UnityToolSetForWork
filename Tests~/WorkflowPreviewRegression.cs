using System;
using System.Collections;
using System.IO;
using System.Reflection;
using ToolSet;
using UnityEditor;
using UnityEngine;

public static class WorkflowPreviewRegression
{
    static int executions;
    static ToolPlan first;
    static ToolPlan second;
    static ToolPlan blocked;

    public static void Run()
    {
        try
        {
            ToolWorkflow.SetActiveOwner("TabA");
            ToolWorkflow.Preview(() => first = Plan("A"));
            EditorApplication.delayCall += PrepareSecond;
        }
        catch (Exception e) { Finish(e); }
    }

    static ToolPlan Plan(string title, bool addReviewProblem = false)
    {
        var plan = new ToolPlan { title = title };
        plan.errors.Add("可跳过的问题");
        plan.Add("有效操作", _ => executions++);
        if (addReviewProblem)
            plan.Add("结果复查", _ => throw new IOException("复查发现残留引用"), keepChangesOnFailure: true);
        return plan;
    }

    static void PrepareSecond()
    {
        try
        {
            ToolWorkflow.SetActiveOwner("TabB");
            ToolWorkflow.Preview(() => second = Plan("B", true));
            EditorApplication.delayCall += VerifyPagesAndStart;
        }
        catch (Exception e) { Finish(e); }
    }

    static void VerifyPagesAndStart()
    {
        try
        {
            var pages = (IDictionary)typeof(ToolWorkflow).GetField("pages", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Require(PagePlan(pages["TabA"]) == first, "TabA 的预览计划未独立保存");
            Require(PagePlan(pages["TabB"]) == second, "TabB 的预览计划未独立保存");
            Require(first != second, "两个页签共用了同一个预览计划");
            ToolWorkflow.Start();
            EditorApplication.update += VerifyExecution;
        }
        catch (Exception e) { Finish(e); }
    }

    static ToolPlan PagePlan(object state) =>
        (ToolPlan)state.GetType().GetField("plan", BindingFlags.Public | BindingFlags.Instance).GetValue(state);

    static void VerifyExecution()
    {
        if (ToolWorkflow.Running) return;
        EditorApplication.update -= VerifyExecution;
        try
        {
            Require(executions == 1, "存在可跳过问题时，有效操作没有执行");
            Require(second.phase == ToolPlanPhase.Succeeded, "含问题的计划未成功完成");
            Require(second.steps[1].state.StartsWith("需处理"), "复查问题未标记为需处理");
            Require(second.errors.Count == 2 && second.errors[1].Contains("残留引用"), "复查问题未写入结果");
            Require(ToolWorkflow.Status.Contains("结果已保留"), "完成状态未说明结果已保留");
            ToolWorkflow.SetActiveOwner("TabC");
            ToolWorkflow.Preview(() =>
            {
                blocked = new ToolPlan { title = "Blocked" };
                blocked.blockingErrors.Add("必须先处理的问题");
                blocked.Add("不应执行", _ => executions++);
                return blocked;
            });
            EditorApplication.delayCall += VerifyBlockedPlan;
        }
        catch (Exception e) { Finish(e); }
    }

    static void VerifyBlockedPlan()
    {
        try
        {
            ToolWorkflow.Start();
            Require(!ToolWorkflow.Running, "含阻断问题的计划仍然开始执行");
            Require(executions == 1, "阻断计划执行了写入步骤");
            Require(blocked.phase == ToolPlanPhase.Ready, "阻断计划状态被错误改变");
            Finish(null);
        }
        catch (Exception e) { Finish(e); }
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void Finish(Exception error)
    {
        string path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "workflow-preview-results.txt");
        File.WriteAllText(path, error == null ? "PASS" : "FAIL\n" + error);
        EditorApplication.Exit(error == null ? 0 : 1);
    }
}
