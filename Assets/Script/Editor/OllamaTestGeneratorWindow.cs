using System;
using System.Text;
using System.IO;
using System.Collections.Generic;
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
    // 関数（メソッド）の情報を安全に保持する構造体
    private struct MethodDefinition
    {
        public string className;
        public string methodName;
        public string signature;
    }

    private string ollamaUrl = "http://localhost:11434/api/generate";
    private string modelName = "codegemma";
    private string savePath = "Assets/Script/Test/Test.cs";
    private string targetDiffPath = "Assets";
    private bool isProcessing = false;

    private UnityWebRequest currentRequest;
    private Action<string> onCommunicationComplete;

    // 個別撃破ループ用の管理変数
    private List<MethodDefinition> targetMethods = new List<MethodDefinition>();
    private StringBuilder finalSpecTable = new StringBuilder();
    private int currentMethodIndex = 0;
    private int globalCaseNumber = 1;

    [MenuItem("Window/Ollama Test Generator")]
    public static void ShowWindow()
    {
        GetWindow<OllamaTestGeneratorWindow>("Ollama Test Gen");
    }

    private void OnGUI()
    {
        GUILayout.Label("Ollama テスト生成パイプライン (個別関数・捏造ゼロ版)", EditorStyles.boldLabel);

        modelName = EditorGUILayout.TextField("使用モデル名", modelName);
        savePath = EditorGUILayout.TextField("保存パス", savePath);
        targetDiffPath = EditorGUILayout.TextField("Diff対象フォルダ", targetDiffPath);

        EditorGUILayout.Space();

        EditorGUI.BeginDisabledGroup(isProcessing);

        // -------------------------------------------------------------
        // フェーズ 1: 仕様書の生成 (ファイル読み込み・関数分割・個別撃破)
        // -------------------------------------------------------------
        GUILayout.Label("【フェーズ 1】", EditorStyles.miniBoldLabel);
        if (GUILayout.Button("1. 変更関数を抽出し、仕様書(_Spec.txt)を自動生成", GUILayout.Height(35)))
        {
            ExecutePhase1();
        }

        EditorGUILayout.Space();

        // -------------------------------------------------------------
        // フェーズ 2: テストコードの生成
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
            if (targetMethods.Count > 0)
            {
                EditorGUILayout.HelpBox($"Ollama思考中... ({currentMethodIndex + 1} / {targetMethods.Count} つ目の関数を処理中)", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Ollamaが通信・思考中です。しばらくお待ちください...", MessageType.Info);
            }
        }
    }

    // =================================================================
    // フェーズ 1: ファイル読み込み ➔ 関数分割 ➔ 個別撃破運用版
    // =================================================================
    private void ExecutePhase1()
    {
        // ① GitDiffからは「変更されたファイル名」のリストだけを安全に取得する (--name-only)
        string gitDiffNameOnly = GetGitDiffNameOnly();
        if (string.IsNullOrEmpty(gitDiffNameOnly))
        {
            EditorUtility.DisplayDialog("通知", "変更されたコードがありませんでした。", "OK");
            return;
        }

        // ② 変更されたファイルを直接開き、中にある関数を構造体リストとしてすべて抽出する
        targetMethods = ExtractAllMethodsFromChangedFiles(gitDiffNameOnly);

        if (targetMethods.Count == 0)
        {
            EditorUtility.DisplayDialog("警告", "変更されたファイルから有効な関数定義（public/private等）が見つかりませんでした。", "OK");
            return;
        }

        // 初期化
        finalSpecTable.Clear();
        finalSpecTable.AppendLine("| 番号 | 対象クラス名 | 対象メソッド名 | テストケース名 | 入力値 | 期待される結果 | 判定理由 |");
        finalSpecTable.AppendLine("|---|---|---|---|---|---|---|");
        currentMethodIndex = 0;
        globalCaseNumber = 1;

        // ③ 個別撃破ループを開始
        ProcessNextMethod();
    }

    // 非同期通信の完了を待って、再帰的にループを回す関数
    private void ProcessNextMethod()
    {
        if (currentMethodIndex >= targetMethods.Count)
        {
            // ⑤ すべての関数の処理が完了したらファイルに一括書き出し
            SaveFinalSpec(finalSpecTable.ToString());
            return;
        }

        MethodDefinition currentMethod = targetMethods[currentMethodIndex];

        // ② ①からテストケース「のみ」を生成する (AIには外枠の型定義しか教えない)
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("あなたはUnityの極めて優秀なQAエンジニアです。以下の【対象関数の引数定義】の型を解析し、高品質なテストケースのバリエーションを日本語のMarkdownの表形式で出力してください。");
        sb.AppendLine();
        sb.AppendLine("## 💡 テストケース設計の必須要件：");
        sb.AppendLine("1. 【同値分割と境界値分析】: 引数のデータ型における上限・下限、およびその境界の前後（0、有効な最大値/最小値、無効な値など）を検証するケースを含めてください。");
        sb.AppendLine("2. 【異常系・エッジケース】: 0（ゼロ）、負の値、データ型の最大値・最小値によるエッジケースを最低1つ以上含めてください。");
        sb.AppendLine();
        sb.AppendLine("❌厳格なルール：絶対にC#のソースコード、クラス、関数などのプログラムコードを出力してはなりません。また、クラス名や関数名を自分で想像して記述することも禁止します。挨拶や補足の解説文も一切不要です。以下のヘッダーに続く表（Table）のデータ行（| テストケース名 | ...）だけを出力してください。");
        sb.AppendLine();
        sb.AppendLine("| テストケース名 | 入力値 | 期待される結果 | 判定理由 |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine();
        sb.AppendLine("【対象関数の引数定義】");
        sb.AppendLine($"引数の情報: {currentMethod.signature}");

        // AIからの返答が到着したあとの処理
        onCommunicationComplete = (response) =>
        {
            // ④ ②と③を合わせてテストケースにする (C#側で安全にドッキング)
            globalCaseNumber = AppendMethodCasesToTable(finalSpecTable, response, currentMethod, globalCaseNumber);

            // 次の関数の処理へ進む
            currentMethodIndex++;
            ProcessNextMethod();
        };

        StartSendPrompt(sb.ToString());
    }

    // 🛠️ Gitから変更された「ファイルパスの一覧」だけを綺麗に貰う（中身は見ない）
    private string GetGitDiffNameOnly()
    {
        try
        {
            string targetPath = string.IsNullOrEmpty(targetDiffPath) ? "Assets" : targetDiffPath;
            System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"diff --name-only -- \"{targetPath}\" \":(exclude)*Editor*\"",
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

    // 🛠️ 変更されたC#ファイルを直接開き、正規表現で関数定義（シグネチャ）を全抽出する
    private List<MethodDefinition> ExtractAllMethodsFromChangedFiles(string nameOnlyDiff)
    {
        List<MethodDefinition> methods = new List<MethodDefinition>();
        string projectRoot = Path.GetDirectoryName(Application.dataPath);

        using (StringReader reader = new StringReader(nameOnlyDiff))
        {
            string filePath;
            while ((filePath = reader.ReadLine()) != null)
            {
                filePath = filePath.Trim();
                if (string.IsNullOrEmpty(filePath) || !filePath.EndsWith(".cs")) continue;

                string fullPath = Path.Combine(projectRoot, filePath);
                if (!File.Exists(fullPath)) continue;

                string currentClass = Path.GetFileNameWithoutExtension(filePath);

                // ファイル全体を上から直接スキャン
                foreach (string fileLine in File.ReadLines(fullPath, Encoding.UTF8))
                {
                    string trimmedLine = fileLine.Trim();

                    // クラス名の正確な特定
                    if (trimmedLine.Contains("class ") && !trimmedLine.StartsWith("//"))
                    {
                        string[] parts = trimmedLine.Split(new[] { "class " }, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            currentClass = parts[1].Split('{', ' ', ':', '\r', '\n')[0].Trim();
                        }
                        continue;
                    }

                    // メソッド定義行の抽出 (中身の数式やreturn文はここで自動的に弾かれる)
                    if ((trimmedLine.StartsWith("public ") || trimmedLine.StartsWith("private ") || trimmedLine.StartsWith("protected "))
                        && trimmedLine.Contains("(") && trimmedLine.Contains(")"))
                    {
                        string signature = trimmedLine.Replace(";", "").Trim();
                        string methodName = "UnknownMethod";
                        try
                        {
                            string[] parts = signature.Split('(');
                            string[] nameParts = parts[0].Split(' ');
                            methodName = nameParts[nameParts.Length - 1].Trim();
                        }
                        catch { continue; }

                        methods.Add(new MethodDefinition
                        {
                            className = currentClass,
                            methodName = methodName,
                            signature = signature
                        });
                    }
                }
            }
        }
        return methods;
    }

    // 🛠️ AIが作ったケース一覧をバラして、確定している本物のクラス名・関数名を流し込む
    private int AppendMethodCasesToTable(StringBuilder tableBuilder, string lLMResponse, MethodDefinition method, int startCaseNumber)
    {
        int caseNumber = startCaseNumber;
        using (StringReader reader = new StringReader(lLMResponse))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("|") && !line.Contains("テストケース名") && !line.Contains("---|"))
                {
                    string trimmedLine = line.TrimStart('|').TrimEnd('|');
                    string[] columns = trimmedLine.Split('|');

                    if (columns.Length >= 4)
                    {
                        string caseName = columns[0].Trim();
                        string inputValue = columns[1].Trim();
                        string expected = columns[2].Trim();
                        string reason = columns[3].Trim();

                        // ★C#側でホールドしていた絶対確実なデータを結合
                        tableBuilder.AppendLine($"| {caseNumber} | {method.className} | {method.methodName} | {caseName} | {inputValue} | {expected} | {reason} |");
                        caseNumber++;
                    }
                }
            }
        }

        // パース失敗時のセーフティ
        if (caseNumber == startCaseNumber)
        {
            tableBuilder.AppendLine($"| {caseNumber} | {method.className} | {method.methodName} | AI出力パースエラー | - | - | {lLMResponse.Replace("\n", " ")} |");
            caseNumber++;
        }

        return caseNumber;
    }

    // 🛠️ ⑤ 最終的な仕様書の保存
    private void SaveFinalSpec(string finalTableText)
    {
        string specPath = savePath.Replace(".cs", "_Spec.txt");
        string folder = Path.GetDirectoryName(specPath);
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

        File.WriteAllText(specPath, finalTableText, Encoding.UTF8);
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("成功", "ステップ1完了！\nファイルを関数ごとに完全分解し、捏造の余地をゼロにした高精度な仕様書(_Spec.txt)を自動生成しました。", "OK");
    }

    // =================================================================
    // フェーズ 2: 仕様書 ➔ テストコード作成 (コンパイルエラー対策版)
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
        sb.AppendLine("2. You MUST use the exact class name from '対象クラス名' and the exact method name from '対象メソッド名' columns specified in the table.");
        sb.AppendLine("3. STATIC METHOD CHECK: If the target method is a static method (or you are calling it directly), call it via 'ClassName.MethodName(...)'. Do NOT use 'new ClassName().MethodName(...)' if it causes a compile error.");
        sb.AppendLine("4. DO NOT WRITE UNCHECKED OVERFLOWS: Never write 'int.MaxValue + 1' or 'int.MinValue - 1' directly in the code as it causes a literal overflow compile error (CS0220). Use checked contexts or variables if needed, or stick to valid type boundaries.");
        sb.AppendLine("5. Output ONLY valid C# code. Do NOT write any markdown blocks like ```csharp.");
        sb.AppendLine("6. Do NOT re-define or implement the source classes to avoid duplicate class errors.");
        sb.AppendLine("7. Do NOT inherit MonoBehaviour.");
        sb.AppendLine();
        sb.AppendLine("## Japanese Test Specification ##");
        sb.AppendLine(specContent);
        sb.AppendLine();
        sb.AppendLine("## C# Code Output (Start from here) ##");
        // ★ using System; と using NUnit.Framework; を先回りして提示
        sb.Append("using System;\nusing NUnit.Framework;\n\n");

        onCommunicationComplete = (response) =>
        {
            // 先回りして削ったヘッダーを合体させて保存
            string codeText = "using System;\nusing NUnit.Framework;\n\n" + TrimText(response);

            string folder = Path.GetDirectoryName(savePath);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            File.WriteAllText(savePath, codeText, Encoding.UTF8);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("成功", $"ステップ2完了！\nコンパイルエラーを修正したテストコードを保存しました:\n{savePath}", "OK");
        };

        StartSendPrompt(sb.ToString());
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
            EndProcessing();
        }
        else
        {
            string jsonResponse = currentRequest.downloadHandler.text;
            OllamaResponse responseData = JsonUtility.FromJson<OllamaResponse>(jsonResponse);

            // 通信が終わったらメイン処理のActionを叩く
            var callback = onCommunicationComplete;
            EndProcessing(); // 次のリクエストのために先にフラグをリセット
            callback?.Invoke(responseData.response);
        }
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

        // 先回りして追加している最上部の using 宣言が AI の出力と重複した場合にお掃除する
        if (text.Contains("using System;"))
        {
            text = text.Replace("using System;", "").Trim();
        }
        if (text.Contains("using NUnit.Framework;"))
        {
            text = text.Replace("using NUnit.Framework;", "").Trim();
        }
        return text.Trim();
    }
    private void OnDestroy() { EndProcessing(); }
}