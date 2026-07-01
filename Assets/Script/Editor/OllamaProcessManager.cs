using System.Diagnostics;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class OllamaProcessManager
{
    // Ollamaの標準的なインストールパス (Windows)
    private static readonly string OllamaPath =
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData)
        + @"\Programs\Ollama\ollama app.exe";

    static OllamaProcessManager()
    {
        // 1. Unityエディタ起動時の処理
        EditorApplication.delayCall += StartOllama;

        // 2. Unityエディタ終了時のイベントに登録
        EditorApplication.quitting += KillOllama;
    }

    // Ollamaの起動
    private static void StartOllama()
    {
        // すでに動いているかチェック（二重起動防止）
        Process[] processes = Process.GetProcessesByName("ollama app");
        if (processes.Length > 0)
        {
            UnityEngine.Debug.Log("【Ollama】すでに起動しています。");
            return;
        }

        if (System.IO.File.Exists(OllamaPath))
        {
            UnityEngine.Debug.Log("【Ollama】Unityエディタ起動に伴い、バックグラウンドサーバーを開始します。");
            Process.Start(OllamaPath);
        }
        else
        {
            UnityEngine.Debug.LogWarning($"【Ollama】指定されたパスにOllamaが見つかりません: {OllamaPath}");
        }
    }

    // Ollamaの強制終了
    private static void KillOllama()
    {
        UnityEngine.Debug.Log("【Ollama】Unityエディタ終了に伴い、バックグラウンドサーバーを停止します。");

        // 「ollama app」と「ollama（本体プロセス）」の両方を落とす
        string[] processNames = { "ollama app", "ollama" };

        foreach (var name in processNames)
        {
            Process[] processes = Process.GetProcessesByName(name);
            foreach (var process in processes)
            {
                try
                {
                    process.Kill(); // プロセスを強制終了
                    process.WaitForExit(1000); // 最大1秒待つ
                }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogError($"【Ollama】プロセスの終了に失敗しました ({name}): {e.Message}");
                }
            }
        }
    }
}