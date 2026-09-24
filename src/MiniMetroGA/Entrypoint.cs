using System;
using System.IO;
using System.Reflection;
using MiniMetroGA;
using MiniMetroGA.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Doorstop
{
    /// <summary>
    /// Ponto de entrada exigido pelo Unity Doorstop (winhttp.dll): a assinatura
    /// tem que ser exatamente `static void Doorstop.Entrypoint.Start()`.
    ///
    /// Por que nao BepInEx e nem Harmony: o Mini Metro foi publicado com managed
    /// stripping agressivo. O mscorlib que acompanha o jogo nao tem
    /// Module.GetPEKind (o BepInEx.Preloader chama na primeira linha) nem o
    /// construtor AmbiguousMatchException(string, Exception) (o cctor de
    /// HarmonyLib.AccessTools chama). Trocar por uma corlib nao-stripada nao
    /// resolve: a que o projeto BepInEx publica e de outro flavor e faz P/Invoke
    /// em System.Native, o que quebra todo o IO do jogo.
    ///
    /// Entao o mod nao faz patching nenhum. Ele so:
    ///   1. se pendura em SceneManager.sceneLoaded (sobreviveu ao stripping);
    ///   2. cria um GameObject com o <see cref="ModBehaviour"/>;
    ///   3. acha a instancia de Game por reflexao, todo frame.
    ///
    /// Start() roda em mono_jit_init, cedo demais para instanciar GameObject,
    /// por isso o bootstrap de verdade so acontece no primeiro sceneLoaded.
    /// </summary>
    public static class Entrypoint
    {
        private static bool _sceneHookInstalled;

        public static void Start()
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                Log.Info("MiniMetroGA " + ModInfo.Version + " iniciando (Doorstop).");
            }
            catch (Exception e)
            {
                Log.Error("Entrypoint falhou: " + e);
            }
        }

        /// <summary>
        /// Esperamos o Assembly-CSharp carregar antes de tocar em qualquer coisa do
        /// Unity. Nesse ponto a engine ja esta de pe e da para assinar o evento de
        /// cena com seguranca.
        /// </summary>
        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                if (_sceneHookInstalled) return;
                // AssemblyLoadEventArgs.LoadedAssembly perdeu o getter no
                // stripping (so sobrou o backing field), entao varremos o dominio
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "Assembly-CSharp") continue;
                    InstallSceneHook();
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Error("OnAssemblyLoad: " + e);
            }
        }

        private static void InstallSceneHook()
        {
            SceneManager.sceneLoaded += (scene, mode) => OnSceneLoaded();
            _sceneHookInstalled = true;
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            Log.Info("Hook de cena instalado.");
        }

        private static void OnSceneLoaded()
        {
            try { ModBootstrap.EnsureHost(); }
            catch (Exception e) { Log.Error("OnSceneLoaded: " + e); }
        }
    }
}

namespace MiniMetroGA
{
    public static class ModInfo
    {
        public const string Guid = "com.claude.minimetro.ga";
        public const string Name = "Mini Metro GA Optimizer";
        public const string Version = "1.0.0";
    }

    public static class ModBootstrap
    {
        private static GameObject _host;

        public static void EnsureHost()
        {
            if (_host != null) return;
            _host = new GameObject("MiniMetroGA");
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _host.AddComponent<ModBehaviour>();
            Log.Info("Host criado.");
            CheckImgui();
        }

        /// <summary>
        /// Confere se a IMGUIModule carregada e a nao-stripada de MiniMetroGA/lib.
        /// Se o dll_search_path_override nao entrou (doorstop_config.ini no
        /// Windows, DOORSTOP_MONO_DLL_SEARCH_PATH_OVERRIDE no Linux), o Mono pega
        /// a do jogo e o painel morre em silencio dentro do OnGUI.
        /// </summary>
        private static void CheckImgui()
        {
            try
            {
                // Nao da para confiar em Assembly.Location: com o override do
                // Doorstop o Mono reporta o caminho de Managed/ mesmo tendo lido
                // o arquivo de lib/. O que vale e o conteudo: a stripada so tem
                // GUILayout.Width e Height.
                var label = typeof(GUILayout).GetMethod("Label",
                    new[] { typeof(string), typeof(GUILayoutOption[]) });
                if (label != null)
                    Log.Info("IMGUIModule nao-stripada OK.");
                else
                    Log.Error("IMGUIModule STRIPADA carregada (sem GUILayout.Label): "
                              + "o search path do Doorstop nao aponta para MiniMetroGA/lib.");
            }
            catch (Exception e) { Log.Warn("CheckImgui: " + e.Message); }
        }
    }

    /// <summary>
    /// Logger de arquivo. Sem BepInEx nao ha console nem log de loader, e o
    /// UnityEngine.Debug so aparece no output_log quando o Doorstop redireciona.
    /// Um arquivo proprio e mais facil de achar quando algo quebra.
    /// </summary>
    public static class Log
    {
        private static readonly object Sync = new object();
        private static readonly System.Collections.Generic.List<string> Tail =
            new System.Collections.Generic.List<string>(64);
        private static string _path;
        public static bool Verbose;

        /// <summary>Ultimas linhas, para a UI mostrar sem abrir o arquivo.</summary>
        public static string[] Recent()
        {
            lock (Sync) return Tail.ToArray();
        }

        private static string Path_
        {
            get
            {
                if (_path != null) return _path;
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(typeof(Log).Assembly.Location);
                    _path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir), "MiniMetroGA.log");
                }
                catch (Exception) { _path = "MiniMetroGA.log"; }
                return _path;
            }
        }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }
        public static void Debug(string msg) { if (Verbose) Write("DEBUG", msg); }

        private static void Write(string level, string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + msg;
            try
            {
                lock (Sync)
                {
                    Tail.Add(line);
                    if (Tail.Count > 200) Tail.RemoveAt(0);
                    // File.AppendAllText foi removido pelo stripping do jogo;
                    // StreamWriter(path, append) sobreviveu.
                    using (var w = new StreamWriter(Path_, true)) w.WriteLine(line);
                }
            }
            catch (Exception) { }
        }
    }
}
