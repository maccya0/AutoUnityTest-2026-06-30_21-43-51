using System;
using System.Text;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;

[Serializable]
public class OllamaRequest
{
    public string model;
    public string prompt;
    public bool stream;
}

[Serializable]
public class OllamaResponse
{
    public string model;
    public string response;
    public bool done;
}

public class OllamaTestGeneratorWindow : EditorWindow
{
    private string ollamaUrl = "http://localhost:11434/api/generate";
    private string modelName = "codegemma";
    private string savePath = "Assets/Script/Test/Test.cs";

    // ★ 開発中のエディタスクリプト自体がテスト対象（ノイズ）になるのを防ぐためのパス制限
    // プロジェクトの構成に合わせて「Assets/Scripts」などロジックのフォルダに変更してください
    private string targetDiffPath = "Assets";

    private bool isProcessing = false;

    private UnityWebRequest currentRequest;
    private Action<string> onCommunicationComplete;

    [MenuItem("Window/Ollama Test Generator")]
    public static void ShowWindow()
    {
        GetWindow<OllamaTestGeneratorWindow>("Ollama Test Gen");
    }

    private void OnGUI()
    {
        GUILayout.Label("Ollama テスト生成パイプライン (高精度・分割版)", EditorStyles.boldLabel);

        modelName = EditorGUILayout.TextField("使用モデル名", modelName);
        savePath = EditorGUILayout.TextField("保存パス", savePath);
        targetDiffPath = EditorGUILayout.TextField("Diff対象フォルダ", targetDiffPath);

        EditorGUILayout.Space();

        EditorGUI.BeginDisabledGroup(isProcessing);

        // -------------------------------------------------------------
        // フェーズ 1: 仕様書の生成 (ステップ ①・②)
        // -------------------------------------------------------------
        GUILayout.Label("【フェーズ 1】", EditorStyles.miniBoldLabel);
        if (GUILayout.Button("1. 境界値・条件網羅を適用した仕様書(_Spec.txt)を生成", GUILayout.Height(35)))
        {
            ExecutePhase1();
        }

        EditorGUILayout.Space();

        // -------------------------------------------------------------
        // フェーズ 2: テストコードの生成 (ステップ ③)
        // -------------------------------------------------------------
        GUILayout.Label("【フェーズ 2】", EditorStyles.miniBoldLabel);
        string specPath = savePath.Replace(".cs", "_Spec.txt");
        bool specExists = File.Exists(specPath);

        EditorGUI.BeginDisabledGroup(!specExists);
        if (GUILayout.Button("2. 仕様書からテストコード(.cs)を自動生成", GUILayout.Height(35)))
        {
            ExecutePhase2(specPath);
        }
        EditorGUI.EndDisabledGroup();

        EditorGUI.EndDisabledGroup();

        if (isProcessing)
        {
            EditorGUILayout.HelpBox("Ollamaが通信・思考中です。しばらくお待ちください...", MessageType.Info);
        }

        if (!specExists)
        {
            EditorGUILayout.HelpBox("まず「1」を実行して仕様書を作成してください（ツール自体の変更差分は除外フォルダ等を指定して避けてください）。", MessageType.Warning);
        }
    }

    // =================================================================
    // フェーズ 1: Git差分 ➔ 仕様書作成 (ステップ ①・②)
    // =================================================================
    private void ExecutePhase1()
    {
        string gitDiff = GetGitDiff();
        if (string.IsNullOrEmpty(gitDiff))
        {
            EditorUtility.DisplayDialog("通知", "指定されたフォルダに変更されたコード（Gitの差分）がありませんでした。", "OK");
            return;
        }

        string cleanDiffDescription = MaskGitDiff(gitDiff);

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("あなたはUnityの極めて優秀なQAエンジニアです。以下の【変更内容】を深く解析し、高品質なテストケースの『仕様一覧』を日本語のMarkdownの表形式で出力してください。");
        sb.AppendLine();
        sb.AppendLine("## 💡 テストケース設計の必須要件（必ずデフォルトで適用すること）：");
        sb.AppendLine("1. 【同値分割と境界値分析】: 単一の代表値だけでなく、数値の比較判定、ループ条件、配列インデックスなどの上限・下限、およびその境界の前後（有効な最大値/最小値、無効な値など）を検証するケースを必ず含めてください。");
        sb.AppendLine("2. 【複数条件網羅（MCC/MCDC準拠）】: IF文の条件式などで AND(&&) や OR(||) が使用されている場合、または複数の入力値が影響し合う場合は、それぞれの条件の真偽(True/False)が組み合わさる主要なバリエーションを網羅させてください。");
        sb.AppendLine("3. 【異常系・エッジケース】: 0（ゼロ）、負の値、null、空文字、配列の境界外、想定外の不正な入力値によってプログラムがクラッシュしないか検証するエッジケースを最低1つ以上含めてください。");
        sb.AppendLine();
        sb.AppendLine("❌厳格なルール：メソッド名を推測、捏造しないこと。");
        sb.AppendLine("❌厳格なルール：絶対にC#のソースコード、クラス、関数などのプログラムコードを出力してはなりません。また、挨拶や補足の解説文も一切不要です。以下のヘッダーに続く表（Table）のデータ行（| 1 | ...）だけを実直に出力してください。");
        sb.AppendLine();
        sb.AppendLine("| 番号 | 対象クラス名 | 対象メソッド名 | テストケース名 | 入力値 | 期待される結果 | 判定理由 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        sb.AppendLine();
        sb.AppendLine("【変更内容】");
        sb.AppendLine(cleanDiffDescription);

        onCommunicationComplete = (response) =>
        {
            string finalResponse = response.Trim();
            if (!finalResponse.StartsWith("|"))
            {
                finalResponse = "| 番号 | 対象クラス名 | 対象メソッド名 | テストケース名 | 入力値 | 期待される結果 | 判定理由 |\n|---|---|---|---|---|---|---|\n" + finalResponse;
            }

            string specPath = savePath.Replace(".cs", "_Spec.txt");
            string folder = Path.GetDirectoryName(specPath);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            File.WriteAllText(savePath, finalResponse, Encoding.UTF8);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("成功", $"ステップ1完了！\n高精度な仕様書を作成しました。\nファイルを確認して調整後、ステップ2へ進んでください。", "OK");
        };

        StartSendPrompt(sb.ToString());
    }
    private string MaskGitDiff(string rawDiff)
    {
        StringBuilder sb = new StringBuilder();
        using (StringReader reader = new StringReader(rawDiff))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("diff") || line.StartsWith("index") || line.StartsWith("---") || line.StartsWith("+++") || line.StartsWith("@@") || line.StartsWith("-"))
                {
                    continue;
                }

                if (line.StartsWith("+"))
                {
                    string content = line.Substring(1).Trim();
                    if (string.IsNullOrEmpty(content) || content == "{" || content == "}") continue;

                    content = content.Replace(";", "");
                    content = content.Replace("public ", "公開関数: ");
                    content = content.Replace("private ", "非公開関数: ");
                    content = content.Replace("static ", "静的: ");
                    content = content.Replace("return ", "戻り値: ");

                    sb.AppendLine($"- 変更追加点: {content}");
                }
            }
        }
        return sb.ToString();
    }

    // =================================================================
    // フェーズ 2: 仕様書 ➔ テストコード作成 (ステップ ③)
    // =================================================================
    private void ExecutePhase2(string specPath)
    {
        if (!File.Exists(specPath)) return;
        string specContent = File.ReadAllText(specPath);

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("You are a Unity QA Engineer. Convert the following Japanese test specification table into a valid NUnit test class.");
        sb.AppendLine();
        sb.AppendLine("## CRITICAL RULES FOR IMPLEMENTATION:");
        sb.AppendLine("1. Implement a dedicated [Test] method for EVERY single row defined in the specification table.");
        sb.AppendLine("2. ⚠️CRITICAL: You MUST use the exact class name from '対象クラス名' and the exact method name from '対象メソッド名' specified in the table columns. Do NOT invent or guess other method names.");
        sb.AppendLine("3. Output ONLY valid C# code. Do NOT write any markdown blocks like ```csharp.");
        sb.AppendLine("4. Do NOT re-define or implement the source classes (e.g. Calculator class) to avoid duplicate class errors.");
        sb.AppendLine("5. Do NOT inherit MonoBehaviour.");
        sb.AppendLine();
        sb.AppendLine("## Japanese Test Specification ##");
        sb.AppendLine(specContent);

        onCommunicationComplete = (response) =>
        {
            string codeText = TrimText(response);

            string folder = Path.GetDirectoryName(savePath);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            File.WriteAllText(savePath, codeText, Encoding.UTF8);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("成功", $"ステップ2完了！\nテストコードを自動生成・保存しました:\n{savePath}", "OK");
        };

        StartSendPrompt(sb.ToString());
    }
    // =================================================================
    // 共通サブシステム（Git取得、通信、トリミング）
    // =================================================================
    private string GetGitDiff()
    {
        try
        {
            // ★ エディタ拡張自身が差分に入らないよう、指定されたフォルダパスの後ろに
            // エディタ（Editor）関連ファイルを明示的に除外するGitの仕組みを付与
            string targetPath = string.IsNullOrEmpty(targetDiffPath) ? "Assets" : targetDiffPath;

            System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                // 指定パスの差分を取りつつ、ツール自身(OllamaTestGeneratorWindowなど)をdiffから完全に遮断する引数
                Arguments = $"diff -- \"{targetPath}\" \":(exclude)*Editor*\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Application.dataPath)
            };

            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return output.Trim();
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"Gitエラー: {e.Message}");
            return string.Empty;
        }
    }

    private void StartSendPrompt(string prompt)
    {
        isProcessing = true;
        OllamaRequest requestData = new OllamaRequest { model = modelName, prompt = prompt, stream = false };
        string jsonPayload = JsonUtility.ToJson(requestData);

        currentRequest = new UnityWebRequest(ollamaUrl, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
        currentRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
        currentRequest.downloadHandler = new DownloadHandlerBuffer();
        currentRequest.SetRequestHeader("Content-Type", "application/json");

        currentRequest.SendWebRequest();
        EditorApplication.update += MonitorWebRequest;
    }

    private void MonitorWebRequest()
    {
        if (currentRequest == null) { EndProcessing(); return; }
        if (!currentRequest.isDone) return;

        if (currentRequest.result == UnityWebRequest.Result.ConnectionError || currentRequest.result == UnityWebRequest.Result.ProtocolError)
        {
            UnityEngine.Debug.LogError($"Ollamaエラー: {currentRequest.error}");
            EditorUtility.DisplayDialog("エラー", $"Ollamaとの通信に失敗しました:\n{currentRequest.error}", "OK");
        }
        else
        {
            string jsonResponse = currentRequest.downloadHandler.text;
            OllamaResponse responseData = JsonUtility.FromJson<OllamaResponse>(jsonResponse);

            onCommunicationComplete?.Invoke(responseData.response);
        }

        EndProcessing();
    }

    private void EndProcessing()
    {
        EditorApplication.update -= MonitorWebRequest;
        if (currentRequest != null) { currentRequest.Dispose(); currentRequest = null; }
        isProcessing = false;
        Repaint();
    }

    private string TrimText(string text)
    {
        text = text.Trim();
        text = text.Replace("```csharp", "").Replace("```", "");

        int startIndex = text.IndexOf("using ");
        if (startIndex == -1) startIndex = text.IndexOf("public class");

        if (startIndex != -1) text = text.Substring(startIndex);
        return text.Trim();
    }

    private void OnDestroy() { EndProcessing(); }
}