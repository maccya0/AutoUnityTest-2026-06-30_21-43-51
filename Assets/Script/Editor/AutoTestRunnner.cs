using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

public static class AutoTestRunner
{
    // これを呼び出すと、EditModeのテストがすべて走る
    public static void RunAllEditModeTests()
    {
        var testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();

        // テスト実行完了時のイベント（コールバック）を登録
        testRunnerApi.RegisterCallbacks(new TestResultCallbacks());

        // 実行設定（EditModeのテストをすべて対象にする）
        var filter = new Filter { testMode = TestMode.EditMode };

        // テスト実行をキック
        testRunnerApi.Execute(new ExecutionSettings(filter));
    }
}

// 📊 テスト結果を受け取るためのコールバッククラス
// 📊 手動テスト実行時の結果を詳細にレポートするコールバック
public class TestResultCallbacks : UnityEditor.TestTools.TestRunner.Api.ICallbacks
{
    // テストごとの結果を蓄積するリスト
    private System.Collections.Generic.List<string> passResults = new System.Collections.Generic.List<string>();
    private System.Collections.Generic.List<string> failResults = new System.Collections.Generic.List<string>();

    public void RunStarted(UnityEditor.TestTools.TestRunner.Api.ITestAdaptor testsToRun)
    {
        Debug.Log("[TestRunner] 手動テストを開始しました...");
        passResults.Clear();
        failResults.Clear();
    }

    public void TestStarted(UnityEditor.TestTools.TestRunner.Api.ITestAdaptor test) { }

    // 💡 各単体テストケースが1つ終わるたびに呼び出される
    public void TestFinished(UnityEditor.TestTools.TestRunner.Api.ITestResultAdaptor result)
    {
        // クラスやアセンブリ単位の通知はスルー（メソッド単位のみを抽出）
        if (result.HasChildren) return;

        // テスト名（メソッド名）を取得
        string testName = result.Name;

        if (result.ResultState == "Failed" || result.ResultState == "Inconclusive")
        {
            // 失敗したケースとエラー理由を記録
            string errorLog = $"❌ {testName}\n   原因: {result.Message.Trim()}";
            failResults.Add(errorLog);
            Debug.LogWarning($"[-] テスト失敗: {testName}\n{result.Message}");
        }
        else if (result.ResultState == "Passed")
        {
            // 成功したケースを記録
            passResults.Add($"✅ {testName}");
        }
    }

    // 💡 すべてのテストが完了したときに一括でダイアログを出す
    public void RunFinished(UnityEditor.TestTools.TestRunner.Api.ITestResultAdaptor result)
    {
        System.Text.StringBuilder report = new System.Text.StringBuilder();

        report.AppendLine("==== 自動生成テスト 実行結果 ====");
        report.AppendLine($"総合スコア: 成功 {result.PassCount} 件 / 失敗 {result.FailCount} 件\n");

        // 1. 失敗したケースがあれば最優先で上に並べる
        if (failResults.Count > 0)
        {
            report.AppendLine("--- ❌ 失敗したテストケース ---");
            foreach (var fail in failResults)
            {
                report.AppendLine(fail);
                report.AppendLine(); // 見やすさのための改行
            }
        }

        // 2. 成功したケースを並べる
        if (passResults.Count > 0)
        {
            report.AppendLine("--- ✅ 成功したテストケース ---");
            foreach (var pass in passResults)
            {
                report.AppendLine(pass);
            }
        }

        // ログにも詳細を吐き出す
        if (result.FailCount > 0)
        {
            Debug.LogError(report.ToString());
            EditorUtility.DisplayDialog("手動テスト結果 (詳細)", report.ToString(), "確認して修正する");
        }
        else
        {
            Debug.Log(report.ToString());
            EditorUtility.DisplayDialog("手動テスト結果 (詳細)", report.ToString(), "素晴らしい！");
        }
    }
}