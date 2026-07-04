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
        GUILayout.Label("Ollama テスト生成パイプライン (捏造防止版)", EditorStyles.boldLabel);

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
    }

    // =================================================================
    // フェーズ 1: Git差分 ➔ 仕様書作成 (ステップ ①・②)
    // =================================================================
    private void ExecutePhase1()
    {
        string gitDiff = GetGitDiff();
        if (string.IsNullOrEmpty(gitDiff))
        {
            EditorUtility.DisplayDialog("通知", "指定されたフォルダに変更されたコードがありませんでした。", "OK");
            return;
        }

        // 数式や中身を完全に消し去った「関数の枠組みだけ」のテキスト
        string cleanDiffDescription = MaskGitDiff(gitDiff);

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("あなたはUnityの極めて優秀なQAエンジニアです。以下の【追加された関数定義】の引数の型を解析し、高品質なテストケースの『仕様一覧』を日本語のMarkdownの表形式で出力してください。");
        sb.AppendLine();
        sb.AppendLine("## 💡 テストケース設計の必須要件（必ずデフォルトで適用すること）：");
        sb.AppendLine("1. 【同値分割と境界値分析】: 引数のデータ型（intなど）における上限・下限、およびその境界の前後（0、有効な最大値/最小値、無効な値など）を検証するケースを必ず含めてください。");
        sb.AppendLine("2. 【異常系・エッジケース】: 0（ゼロ）、負の値、データ型の最大値・最小値によってオーバーフローや予期せぬ挙動が起きないか検証するエッジケースを必ず含めてください。");
        sb.AppendLine();
        sb.AppendLine("## ⚠️ メソッド名・クラス名の絶対厳守ルール：");
        sb.AppendLine("・【対象メソッド名】の列には、以下に書かれている【実際のメソッド名】を1文字も変えずにそのまま記述してください。絶対に別の名前に書き換えてはなりません。");
        sb.AppendLine();
        sb.AppendLine("❌厳格なルール：絶対にC#のソースコード、クラス、関数などのプログラムコードを出力してはなりません。また、挨拶や補足の解説文も一切不要です。以下のヘッダーに続く表（Table）のデータ行（| 1 | ...）だけを実直に出力してください。");
        sb.AppendLine();
        sb.AppendLine("| 番号 | 対象クラス名 | 対象メソッド名 | テストケース名 | 入力値 | 期待される結果 | 判定理由 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        sb.AppendLine();
        sb.AppendLine("【追加された関数定義】");
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

            File.WriteAllText(specPath, finalResponse, Encoding.UTF8);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("成功", $"ステップ1完了！\n仕様書を作成しました（_Spec.txt）。\n中身を確認・修正して、ステップ2へ進んでください。", "OK");
        };

        StartSendPrompt(sb.ToString());
    }

    // ★ 中身の数式（return a * b; など）を徹底的に排除する関数
    private string MaskGitDiff(string rawDiff)
    {
        StringBuilder sb = new StringBuilder();
        string currentClass = "UnknownClass";

        using (StringReader reader = new StringReader(rawDiff))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                // クラス名の抽出を試みる
                if (line.Contains("class "))
                {
                    string[] parts = line.Split(new[] { "class " }, StringSplitOptions.None);
                    if (parts.Length > 1)
                    {
                        currentClass = parts[1].Split('{', ' ', ':')[0].Trim();
                    }
                }

                if (line.StartsWith("diff") || line.StartsWith("index") || line.StartsWith("---") || line.StartsWith("+++") || line.StartsWith("@@") || line.StartsWith("-"))
                {
                    continue;
                }

                if (line.StartsWith("+"))
                {
                    string content = line.Substring(1).Trim();

                    // ★ 戻り値の計算内容（return ...）やブラケットは「AIの誤認誘発ブロック」として完全に無視
                    if (string.IsNullOrEmpty(content) || content == "{" || content == "}" || content.StartsWith("return"))
                        continue;

                    // メソッドの定義行（シグネチャ）だけを抽出して分かりやすくする
                    if (content.Contains("public ") || content.Contains("private ") || content.Contains("protected "))
                    {
                        sb.AppendLine($"- 対象クラス名: {currentClass}");
                        sb.AppendLine($"- 追加された実際の関数シグネチャ: {content.Replace(";", "")}");
                    }
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
        sb.AppendLine("2. You MUST use the exact class name from '対象クラス名' and the exact method name from '対象メソッド名' columns specified in the table. Do NOT invent or guess other names.");
        sb.AppendLine("3. Output ONLY valid C# code. Do NOT write any markdown blocks like ```csharp.");
        sb.AppendLine("4. Do NOT re-define or implement the source classes to avoid duplicate class errors.");
        sb.AppendLine("5. Do NOT inherit MonoBehaviour.");
        sb.AppendLine();
        sb.AppendLine("## Japanese Test Specification ##");
        sb.AppendLine(specContent);
        sb.AppendLine();
        sb.AppendLine("## C# Code Output (Start from here) ##");
        // ★ AIに表のオウム返しをさせず、確実にC#コードを書かせるための先回りコード補完
        sb.Append("using NUnit.Framework;\n\n");

        onCommunicationComplete = (response) =>
        {
            // 先回りして削ったヘッダーを合体させて保存
            string codeText = "using NUnit.Framework;\n\n" + TrimText(response);

            string folder = Path.GetDirectoryName(savePath);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            File.WriteAllText(savePath, codeText, Encoding.UTF8);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("成功", $"ステップ2完了！\nテストコードを自動生成・保存しました:\n{savePath}", "OK");
        };

        StartSendPrompt(sb.ToString());
    }

    private string GetGitDiff()
    {
        try
        {
            string targetPath = string.IsNullOrEmpty(targetDiffPath) ? "Assets" : targetDiffPath;
            System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
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

        // もし先回りのusingが重複して返ってきたらお掃除する
        if (text.StartsWith("using NUnit.Framework;"))
        {
            text = text.Substring("using NUnit.Framework;".Length).Trim();
        }
        return text.Trim();
    }

    private void OnDestroy() { EndProcessing(); }
}