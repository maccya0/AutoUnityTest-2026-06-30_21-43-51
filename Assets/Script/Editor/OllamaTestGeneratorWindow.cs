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
    private bool isProcessing = false;

    private UnityWebRequest currentRequest;
    private Action<string> onCommunicationComplete; // 通信完了時に実行する処理

    [MenuItem("Window/Ollama Test Generator")]
    public static void ShowWindow()
    {
        GetWindow<OllamaTestGeneratorWindow>("Ollama Test Gen");
    }

    private void OnGUI()
    {
        GUILayout.Label("Ollama テスト生成パイプライン (分割運用版)", EditorStyles.boldLabel);

        modelName = EditorGUILayout.TextField("使用モデル名", modelName);
        savePath = EditorGUILayout.TextField("保存パス", savePath);

        EditorGUILayout.Space();

        // 共通の処理中ガード
        EditorGUI.BeginDisabledGroup(isProcessing);

        // -------------------------------------------------------------
        // フェーズ 1: 仕様書の生成 (ステップ ①・②)
        // -------------------------------------------------------------
        GUILayout.Label("【フェーズ 1】", EditorStyles.miniBoldLabel);
        if (GUILayout.Button("1. Git差分からテスト仕様書(_Spec.txt)を生成", GUILayout.Height(35)))
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

        // 仕様書ファイルが存在しない場合はボタンを押せなくする
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
            EditorGUILayout.HelpBox("まずは「1」を実行して仕様書を作成してください。", MessageType.Warning);
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
            EditorUtility.DisplayDialog("通知", "変更されたコード（Gitの差分）がありませんでした。", "OK");
            return;
        }

        // ★ AIの勘違い（C#コード化）を防ぐため、diffの生テキストをただの変更点テキストに超圧縮する
        string cleanDiffDescription = MaskGitDiff(gitDiff);

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("あなたはUnityのQAエンジニアです。以下の【変更内容】だけを読み、テストケースの『仕様一覧』のみを日本語のMarkdownの表形式で出力してください。");
        sb.AppendLine("❌厳格なルール：絶対にC#のソースコード、クラス、関数などを出力してはなりません。また、挨拶や説明文も一切不要です。表（Table）だけを出力してください。");
        sb.AppendLine();
        sb.AppendLine("【変更内容】");
        sb.AppendLine(cleanDiffDescription);
        sb.AppendLine();
        sb.AppendLine("| 番号 | テストケース名 | 入力値 | 期待される結果 | 判定理由 |");
        sb.AppendLine("|---|---|---|---|---|");

        onCommunicationComplete = (response) =>
        {
            string finalResponse = response.Trim();
            if (!finalResponse.StartsWith("|"))
            {
                finalResponse = "| 番号 | テストケース名 | 入力値 | 期待される結果 | 判定理由 |\n|---|---|---|---|---|\n" + finalResponse;
            }

            string specPath = savePath.Replace(".cs", "_Spec.txt");
            string folder = Path.GetDirectoryName(specPath);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            File.WriteAllText(specPath, finalResponse, Encoding.UTF8);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("成功", $"ステップ1完了！\n仕様書を作成しました。", "OK");
        };

        StartSendPrompt(sb.ToString());
    }

    // AIにC#コードと誤認させないための、差分情報のテキスト化関数
    private string MaskGitDiff(string rawDiff)
    {
        StringBuilder sb = new StringBuilder();
        using (StringReader reader = new StringReader(rawDiff))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                // gitのヘッダー情報（@@ や diff --git など）や、削除された行（-）は無視
                if (line.StartsWith("diff") || line.StartsWith("index") || line.StartsWith("---") || line.StartsWith("+++") || line.StartsWith("@@") || line.StartsWith("-"))
                {
                    continue;
                }

                // 追加された行（+）だけを抽出
                if (line.StartsWith("+"))
                {
                    string content = line.Substring(1).Trim();
                    if (string.IsNullOrEmpty(content) || content == "{" || content == "}") continue;

                    // コードの記号（; や public, using など）を徹底的に削るか、ただの文字にする
                    content = content.Replace(";", "");
                    content = content.Replace("public ", "公開関数: ");
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

        // AIへの指示：確定した日本語の仕様書を、そのまま1対1でC#のコードに翻訳させる
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("You are a Unity QA Engineer. Convert the following Japanese test specification into a valid NUnit test class.");
        sb.AppendLine("Rule 1: Output ONLY valid C# code. Do NOT write any markdown blocks like ```csharp.");
        sb.AppendLine("Rule 2: Do NOT re-define or implement the source classes (e.g. Calculator class) to avoid duplicate class errors.");
        sb.AppendLine("Rule 3: Do NOT inherit MonoBehaviour.");
        sb.AppendLine();
        sb.AppendLine("## Japanese Test Specification ##");
        sb.AppendLine(specContent);

        onCommunicationComplete = (response) =>
        {
            // ③ テストコードを掃除して保存
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
            System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "diff",
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

            // コールバック経由で各フェーズの保存処理を実行
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