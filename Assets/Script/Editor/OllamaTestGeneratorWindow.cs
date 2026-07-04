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
        GUILayout.Label("Ollama テスト生成パイプライン (手動実行版)", EditorStyles.boldLabel);

        modelName = EditorGUILayout.TextField("使用モデル名", modelName);
        savePath = EditorGUILayout.TextField("保存パス", savePath);
        targetDiffPath = EditorGUILayout.TextField("Diff対象フォルダ", targetDiffPath);

        EditorGUILayout.Space();

        EditorGUI.BeginDisabledGroup(isProcessing);

        // -------------------------------------------------------------
        // フェーズ 1: 仕様書の生成
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

        EditorGUILayout.Space();

        // -------------------------------------------------------------
        // フェーズ 3: テストの手動実行 (★新設)
        // -------------------------------------------------------------
        GUILayout.Label("【フェーズ 3】", EditorStyles.miniBoldLabel);
        bool testCodeExists = File.Exists(savePath);

        // Unityが現在コンパイル中（裏でぐるぐる中）かどうかもチェックし、コンパイル中はボタンを押せなくする
        bool isCompiling = EditorApplication.isCompiling;

        EditorGUI.BeginDisabledGroup(!testCodeExists || isCompiling);

        string buttonText = isCompiling ? "Unityコンパイル中..." : "3. 生成されたテストを手動実行 (Test Runner)";
        if (GUILayout.Button(buttonText, GUILayout.Height(35)))
        {
            RunAllEditModeTests();
        }
        EditorGUI.EndDisabledGroup();

        if (isCompiling)
        {
            EditorGUILayout.HelpBox("Unityが新しいスクリプトをコンパイル（ビルド）しています。終わるまで少々お待ちください...", MessageType.Warning);
        }

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

    private void ExecutePhase1()
    {
        string gitDiffNameOnly = GetGitDiffNameOnly();
        if (string.IsNullOrEmpty(gitDiffNameOnly))
        {
            EditorUtility.DisplayDialog("通知", "変更されたコードがありませんでした。", "OK");
            return;
        }

        targetMethods = ExtractAllMethodsFromChangedFiles(gitDiffNameOnly);

        if (targetMethods.Count == 0)
        {
            EditorUtility.DisplayDialog("警告", "変更されたファイルから有効な関数定義が見つかりませんでした。", "OK");
            return;
        }

        finalSpecTable.Clear();
        finalSpecTable.AppendLine("| 番号 | 対象クラス名 | 対象メソッド名 | テストケース名 | 入力値 | 期待される結果 | 判定理由 |");
        finalSpecTable.AppendLine("|---|---|---|---|---|---|---|");
        currentMethodIndex = 0;
        globalCaseNumber = 1;

        ProcessNextMethod();
    }

    private void ProcessNextMethod()
    {
        if (currentMethodIndex >= targetMethods.Count)
        {
            SaveFinalSpec(finalSpecTable.ToString());
            return;
        }

        MethodDefinition currentMethod = targetMethods[currentMethodIndex];

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

        onCommunicationComplete = (response) =>
        {
            globalCaseNumber = AppendMethodCasesToTable(finalSpecTable, response, currentMethod, globalCaseNumber);
            currentMethodIndex++;
            ProcessNextMethod();
        };

        StartSendPrompt(sb.ToString());
    }

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

                foreach (string fileLine in File.ReadLines(fullPath, Encoding.UTF8))
                {
                    string trimmedLine = fileLine.Trim();

                    if (trimmedLine.Contains("class ") && !trimmedLine.StartsWith("//"))
                    {
                        string[] parts = trimmedLine.Split(new[] { "class " }, StringSplitOptions.None);
                        if (parts.Length > 1)
                        {
                            currentClass = parts[1].Split('{', ' ', ':', '\r', '\n')[0].Trim();
                        }
                        continue;
                    }

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

                        tableBuilder.AppendLine($"| {caseNumber} | {method.className} | {method.methodName} | {caseName} | {inputValue} | {expected} | {reason} |");
                        caseNumber++;
                    }
                }
            }
        }

        if (caseNumber == startCaseNumber)
        {
            tableBuilder.AppendLine($"| {caseNumber} | {method.className} | {method.methodName} | AI出力パースエラー | - | - | {lLMResponse.Replace("\n", " ")} |");
            caseNumber++;
        }

        return caseNumber;
    }

    private void SaveFinalSpec(string finalTableText)
    {
        string specPath = savePath.Replace(".cs", "_Spec.txt");
        string folder = Path.GetDirectoryName(specPath);
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

        File.WriteAllText(specPath, finalTableText, Encoding.UTF8);
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("成功", "ステップ1完了！\n仕様書(_Spec.txt)を手動用に自動生成しました。", "OK");
    }

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
        sb.AppendLine("3. STATIC METHOD CHECK: If the target method is a static method, call it via 'ClassName.MethodName(...)'. Do NOT use 'new ClassName().MethodName(...)'.");
        sb.AppendLine("4. DO NOT WRITE UNCHECKED OVERFLOWS: Never write 'int.MaxValue + 1' or 'int.MinValue - 1' directly in the code as it causes a literal overflow compile error (CS0220).");
        sb.AppendLine("5. Output ONLY valid C# code. Do NOT write any markdown blocks like ```csharp.");
        sb.AppendLine("6. Do NOT re-define or implement the source classes to avoid duplicate class errors.");
        sb.AppendLine("7. Do NOT inherit MonoBehaviour.");
        sb.AppendLine();
        sb.AppendLine("## Japanese Test Specification ##");
        sb.AppendLine(specContent);
        sb.AppendLine();
        sb.AppendLine("## C# Code Output (Start from here) ##");
        sb.Append("using System;\nusing NUnit.Framework;\n\n");

        onCommunicationComplete = (response) =>
        {
            string codeText = "using System;\nusing NUnit.Framework;\n\n" + TrimText(response);

            string folder = Path.GetDirectoryName(savePath);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            File.WriteAllText(savePath, codeText, Encoding.UTF8);

            // アセット更新（勝手にテストを走らせるフラグは撤廃）
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("成功", $"ステップ2完了！\nテストコードを生成しました。Unityのコンパイルが完了したら『3』のボタンから手動実行できます。\n{savePath}", "OK");
        };

        StartSendPrompt(sb.ToString());
    }

    // ★ ボタンから手動で呼び出せるテスト実行メソッド
    private static void RunAllEditModeTests()
    {
        var testRunnerApi = ScriptableObject.CreateInstance<UnityEditor.TestTools.TestRunner.Api.TestRunnerApi>();
        testRunnerApi.RegisterCallbacks(new TestResultCallbacks());

        var filter = new UnityEditor.TestTools.TestRunner.Api.Filter
        {
            testMode = UnityEditor.TestTools.TestRunner.Api.TestMode.EditMode
        };

        testRunnerApi.Execute(new UnityEditor.TestTools.TestRunner.Api.ExecutionSettings(filter));
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

            var callback = onCommunicationComplete;
            EndProcessing();
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

        if (text.Contains("using System;")) text = text.Replace("using System;", "").Trim();
        if (text.Contains("using NUnit.Framework;")) text = text.Replace("using NUnit.Framework;", "").Trim();
        return text.Trim();
    }

    private void OnDestroy() { EndProcessing(); }
}
