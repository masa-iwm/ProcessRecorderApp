using Microsoft.Windows.AppLifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProcessRecorderApp.SingleInstance;

/// <summary>
/// 常駐ワーカーが受け取った起動引数（コマンドライン文字列）を、
/// アプリ側のコマンドパーサーに渡せる引数配列（string[]）へ変換する。
/// ここではあくまで文字列の分解のみを行い、コマンド名・オプションの意味づけは行わない。
/// </summary>
internal static class ActivationTokenizer
{
    /// <summary>
    /// AppActivationArguments から、起動時のコマンドライン文字列を取り出す。
    /// アンパッケージWin32アプリの Launch アクティブ化の場合、この文字列は
    /// <c>Environment.CommandLine</c> 相当（argv[0]にあたる自身のexeパスを含む）になる。
    /// 先頭のexeパスは <see cref="StripExecutablePath"/> で除去する。
    /// </summary>
    public static string ExtractCommandLine(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.Launch &&
            args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs)
        {
            return launchArgs.Arguments ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// コマンドライン文字列を空白区切りでトークン化し、
    /// アプリ側のコマンドパーサーに渡せる配列にする。
    /// 二重引用符で囲まれた区間はスペースを含めて1トークンとして扱う
    /// （例: --process "C:\path with space\file.txt"）。
    /// </summary>
    public static string[] Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [.. tokens];
        }

        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (char c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return [.. tokens];
    }

    /// <summary>
    /// トークン配列の先頭が自プロセスの実行ファイル（argv[0]相当）を指している場合、
    /// それを取り除いた配列を返す。アンパッケージWin32アプリの Launch アクティブ化では
    /// 起動引数の文字列がexeパスを含んだ完全なコマンドラインになるため、コマンドパーサーへ
    /// 渡す前に除去しておく必要がある。
    /// exeを直接実行した場合はフルパス（例: <c>G:\...\ProcessRecorderApp.exe</c>）、
    /// Visual Studioのデバッグ実行の場合はファイル名のみ（例: <c>ProcessRecorderApp.exe</c>）が
    /// 設定されるなど、環境によって形式が異なるため、ファイル名部分のみを比較する
    /// （一致しない場合はそのまま返す）。
    /// </summary>
    public static string[] StripExecutablePath(string[] tokens)
    {
        if (tokens.Length == 0)
        {
            return tokens;
        }

        string? exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            return tokens;
        }

        string firstTokenFileName = Path.GetFileName(tokens[0]);
        string exeFileName = Path.GetFileName(exePath);
        if (string.Equals(firstTokenFileName, exeFileName, StringComparison.OrdinalIgnoreCase))
        {
            return tokens[1..];
        }

        return tokens;
    }
}
