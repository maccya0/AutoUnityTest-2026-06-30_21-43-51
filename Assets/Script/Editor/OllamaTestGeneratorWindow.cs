using System;
using System.Text;
using System.IO;
using System.Diagnostics;
using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;

// Ollamaに送るリクエストデータ
[Serializable]
public class OllamaRequest
{
    public string model;
    public string prompt;
    public bool stream;
}

// Ollamaから返ってくるレスポンスデータ
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

    [MenuItem("Window/Ollama Test Generator")]
    public static void ShowWindow()
    {
        GetWindow<OllamaTestGeneratorWindow>("Ollama Test Gen");
    }

    private void OnGUI()
    {
        GUILayout.Label("Ollama テストコード自動生成 (Git差分連動)", EditorStyles.boldLabel);

        modelName = EditorGUILayout.TextField("使用モデル名", modelName);
        savePath = EditorGUILayout.TextField("保存パス", savePath);

        EditorGUILayout.Space();

        // 処理中はボタンを無効化
        EditorGUI.BeginDisabledGroup(isProcessing);
        if (GUILayout.Button("現在のGit差分からテストを生成", GUILayout.Height(40)))
        {
            ExecutePipeline();
        }
        EditorGUI.EndDisabledGroup();

        if (isProcessing)
        {
            EditorGUILayout.HelpBox("Git差分を解析中、およびOllamaが思考中...", MessageType.Info);
        }
    }

    // 差分取得からAPI送信までの一連の流れ
    private void ExecutePipeline()
    {
        // 1. Gitの差分を取得
        string gitDiff = GetGitDiff();

        if (string.IsNullOrEmpty(gitDiff))
        {
            UnityEngine.Debug.LogWarning("【Ollama】Gitの差分（変更点）が見つかりませんでした。コードを書き換えてから実行してください。");
            EditorUtility.DisplayDialog("通知", "変更されたコード（Gitの差分）がありませんでした。", "OK");
            return;
        }

        // 2. 差分を埋め込んだプロンプトを自動生成
        string finalPrompt = BuildPrompt(gitDiff);

        // 3. Ollamaへ送信
        StartSendPrompt(finalPrompt);
    }

    // C#からローカルのGitを呼び出して「git diff」を取得する関数
    private string GetGitDiff()
    {
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "diff", // コミット前のステージング・未ステージングの差分を取得
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Unityプロジェクトのルートフォルダ（Assetsの1つ上）を作業ディレクトリにする
                WorkingDirectory = Path.GetDirectoryName(Application.dataPath)
            };

            using (Process process = Process.Start(startInfo))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrEmpty(error) && !error.Contains("warning"))
                {
                    UnityEngine.Debug.LogError($"Gitエラー: {error}");
                }

                return output.Trim();
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"Gitの実行に失敗しました。GitがPCにインストールされ、環境パスが通っているか確認してください: {e.Message}");
            return string.Empty;
        }
    }

    // プロンプトを組み立てる関数
    private string BuildPrompt(string diffText)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("あなたはUnityの優秀なQAエンジニアです。");
        sb.AppendLine("提供されたC#コードの「Git差分（変更点）」を確認し、追加・修正された関数やロジックに対するUnity Test Runner（NUnit / EditMode）用のテストケースをC#で出力してください。");
        sb.AppendLine();
        sb.AppendLine("## 制約事項：");
        sb.AppendLine("- 余計な解説文や「```csharp」などのマークダウン装飾は一切含めず、純粋なC#のコードブロックのみを出力してください。");
        sb.AppendLine("- 必要な名前空間（using NUnit.Framework;, using UnityEngine; など）を必ず含めてください。");
        sb.AppendLine();
        sb.AppendLine("## コードの変更差分（git diff）：");
        sb.AppendLine(diffText);

        return sb.ToString();
    }

    // 通信の開始処理
    private void StartSendPrompt(string prompt)
    {
        isProcessing = true;

        OllamaRequest requestData = new OllamaRequest
        {
            model = modelName,
            prompt = prompt,
            stream = false
        };

        string jsonPayload = JsonUtility.ToJson(requestData);

        currentRequest = new UnityWebRequest(ollamaUrl, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
        currentRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
        currentRequest.downloadHandler = new DownloadHandlerBuffer();
        currentRequest.SetRequestHeader("Content-Type", "application/json");

        currentRequest.SendWebRequest();
        EditorApplication.update += MonitorWebRequest;
    }

    // 毎フレーム呼び出され、通信が終わったかチェックする関数
    private void MonitorWebRequest()
    {
        if (currentRequest == null)
        {
            EndProcessing();
            return;
        }

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

            string text = responseData.response;

            // 不要な文字をカット
            text = TrimText(text);

            // フォルダ自動作成
            string directory = Path.GetDirectoryName(savePath);
            if (!Directory.Exists(directory) && !string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // ファイル書き込み
            File.WriteAllText(savePath, text);
            AssetDatabase.Refresh();

            UnityEngine.Debug.Log($"【Ollama】Git差分からテストコードを生成・保存しました: {savePath}");
            EditorUtility.DisplayDialog("成功", "Git差分の解析とテストコードの自動生成が完了しました！", "OK");
        }

        EndProcessing();
    }

    private void EndProcessing()
    {
        EditorApplication.update -= MonitorWebRequest;
        if (currentRequest != null)
        {
            currentRequest.Dispose();
            currentRequest = null;
        }
        isProcessing = false;
        Repaint();
    }

    private void OnDestroy()
    {
        EndProcessing();
    }

    private string TrimText(string text)
    {
        if (text.Contains("```"))
        {
            int firstIdx = text.IndexOf("```");
            int startIdx = text.IndexOf("\n", firstIdx) + 1;
            int lastIdx = text.LastIndexOf("```");

            if (lastIdx > startIdx)
            {
                text = text.Substring(startIdx, lastIdx - startIdx);
            }
        }

        text = text.Replace("```csharp", "");
        text = text.Replace("```", "");
        text = text.Trim();
        return text;
    }
}