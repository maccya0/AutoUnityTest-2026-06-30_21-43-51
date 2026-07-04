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
    // フェーズ 1: 5ステップ分割運用版 (仕様書作成)
    // =================================================================
    private void ExecutePhase1()
    {
        // -------------------------------------------------------------
        // ① GitDiffで差分を取る
        // -------------------------------------------------------------
        string gitDiff = GetGitDiff();
        if (string.IsNullOrEmpty(gitDiff))
        {
            EditorUtility.DisplayDialog("通知", "変更されたコードがありませんでした。", "OK");
            return;
        }

        // -------------------------------------------------------------
        // ③ クラス名と関数名を取得する (C#側で100%正確に特定)
        // -------------------------------------------------------------
        string targetClass = "UnknownClass";
        string targetMethod = "UnknownMethod";
        string methodSignature = "";

        // 事前に差分からクラス名とメソッド名を厳密に抽出する
        ExtractClassAndMethod(gitDiff, out targetClass, out targetMethod, out methodSignature);

        if (targetClass == "UnknownClass" || targetMethod == "UnknownMethod")
        {
            EditorUtility.DisplayDialog("警告", "差分からクラス名またはメソッド名を特定できませんでした。通常のAssets全体で再試行するか、コミット状態を確認してください。", "OK");
            return;
        }

        // -------------------------------------------------------------
        // ② ①からテストケース「のみ」を生成する (LLMへのプロンプト)
        // -------------------------------------------------------------
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("あなたはUnityの極めて優秀なQAエンジニアです。以下の【対象関数の引数定義】の型を解析し、高品質なテストケースのバリエーションを日本語のMarkdownの表形式で出力してください。");
        sb.AppendLine();
        sb.AppendLine("## 💡 テストケース設計の必須要件（必ずデフォルトで適用すること）：");
        sb.AppendLine("1. 【同値分割と境界値分析】: 引数のデータ型における上限・下限、およびその境界の前後（0、有効な最大値/最小値、無効な値など）を検証するケースを必ず含めてください。");
        sb.AppendLine("2. 【異常系・エッジケース】: 0（ゼロ）、負の値、データ型の最大値・最小値によってオーバーフローや予期せぬ挙動が起きないか検証するエッジケースを必ず含めてください。");
        sb.AppendLine();
        sb.AppendLine("❌厳格なルール：絶対にC#のソースコード、クラス、関数などのプログラムコードを出力してはなりません。また、クラス名や関数名を自分で想像して記述することも禁止します。挨拶や補足の解説文も一切不要です。以下のヘッダーに続く表（Table）のデータ行（| テストケース名 | ...）だけを実直に出力してください。");
        sb.AppendLine();
        sb.AppendLine("| テストケース名 | 入力値 | 期待される結果 | 判定理由 |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine();
        sb.AppendLine("【対象関数の引数定義】");
        sb.AppendLine($"引数の情報: {methodSignature}");

        // LLMからの返答が来答したあとの処理
        onCommunicationComplete = (response) =>
        {
            // -------------------------------------------------------------
            // ④ ②と③を合わせてテストケースにする (C#側でガッチャンコ)
            // -------------------------------------------------------------
            string finalTable = CombineAndBuildFinalTable(response, targetClass, targetMethod);

            // -------------------------------------------------------------
            // ⑤ ④を出力する
            // -------------------------------------------------------------
            string specPath = savePath.Replace(".cs", "_Spec.txt");
            string folder = Path.GetDirectoryName(specPath);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            File.WriteAllText(specPath, finalTable, Encoding.UTF8);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("成功", $"ステップ1完了！\n5ステップ分割により、捏造のない正確な仕様書を作成しました（_Spec.txt）。", "OK");
        };

        StartSendPrompt(sb.ToString());
    }

    // 🛠️ 【ステップ③の実装】差分とファイルからクラス名と関数名を「絶対に嘘をつけないC#」で抜き出す
    private void ExtractClassAndMethod(string rawDiff, out string className, out string methodName, out string signature)
    {
        className = "UnknownClass";
        methodName = "UnknownMethod";
        signature = "";

        using (StringReader reader = new StringReader(rawDiff))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("+++ b/"))
                {
                    string filePath = line.Substring(6).Trim();
                    className = Path.GetFileNameWithoutExtension(filePath);

                    string fullPath = Path.Combine(Directory.GetCurrentDirectory(), filePath);
                    if (File.Exists(fullPath))
                    {
                        foreach (string fileLine in File.ReadLines(fullPath))
                        {
                            if (fileLine.Contains("class ") && !fileLine.Contains("//"))
                            {
                                string[] parts = fileLine.Split(new[] { "class " }, StringSplitOptions.None);
                                if (parts.Length > 1)
                                {
                                    className = parts[1].Split('{', ' ', ':', '\r', '\n')[0].Trim();
                                    break;
                                }
                            }
                        }
                    }
                }

                if (line.StartsWith("+"))
                {
                    string content = line.Substring(1).Trim();
                    if (string.IsNullOrEmpty(content) || content == "{" || content == "}" || content.StartsWith("return"))
                        continue;

                    if (content.Contains("public ") || content.Contains("private ") || content.Contains("protected "))
                    {
                        signature = content.Replace(";", "").Trim();

                        // シグネチャから関数名を抜き出す (例: Add(int a, int b) ➔ Add)
                        try
                        {
                            string[] parts = signature.Split('(');
                            string[] nameParts = parts[0].Split(' ');
                            methodName = nameParts[nameParts.Length - 1].Trim();
                        }
                        catch
                        {
                            methodName = "UnknownMethod";
                        }
                    }
                }
            }
        }
    }

    // 🛠️ 【ステップ④の実装】LLMが作ったケース一覧に、確定したクラス名と関数名を横から流し込む
    private string CombineAndBuildFinalTable(string lLMResponse, string className, string methodName)
    {
        StringBuilder sb = new StringBuilder();
        // 完成形のヘッダーを出力
        sb.AppendLine("| 番号 | 対象クラス名 | 対象メソッド名 | テストケース名 | 入力値 | 期待される結果 | 判定理由 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");

        int caseNumber = 1;
        using (StringReader reader = new StringReader(lLMResponse))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                // 有効なデータ行（| で始まっていて、ヘッダーや区切り線ではない行）を判定
                if (line.StartsWith("|") && !line.Contains("テストケース名") && !line.Contains("---|"))
                {
                    // LLMが返してきた行は 「| ケース名 | 入力値 | 期待値 | 理由 |」 になっている
                    // これをバラして、頭に「番号」「クラス名」「関数名」をドッキングする
                    string trimmedLine = line.TrimStart('|').TrimEnd('|');
                    string[] columns = trimmedLine.Split('|');

                    if (columns.Length >= 4)
                    {
                        string caseName = columns[0].Trim();
                        string inputValue = columns[1].Trim();
                        string expected = columns[2].Trim();
                        string reason = columns[3].Trim();

                        sb.AppendLine($"| {caseNumber} | {className} | {methodName} | {caseName} | {inputValue} | {expected} | {reason} |");
                        caseNumber++;
                    }
                }
            }
        }

        // 万が一、LLMの出力フォーマットが崩れて1行もパースできなかった場合のセーフティバッファ
        if (caseNumber == 1)
        {
            sb.AppendLine($"| 1 | {className} | {methodName} | ※パースエラー。LLMの生出力を確認してください | - | - | {lLMResponse.Replace("\n", " ")} |");
        }

        return sb.ToString();
    }    // =================================================================
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