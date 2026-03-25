using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ConduitNet;
using Unity.WebRTC;
using UnityEditor;
using UnityEngine;

namespace ConduitNet.Editor {
    public class ConduitMonitorWindow : EditorWindow {
        private Vector2 scrollPos;
        private double lastRepaintTime;
        private double lastRttPollTime;
        private double lastStatusPollTime;

        private readonly Dictionary<string, List<float>> peerRttHistory = new();
        private readonly List<float> serverLatencyHistory = new();
        private const int MaxHistory = 50;

        // Foldout states
        private bool showSignaling = true;
        private bool showServerStatus = true;
        private bool showLobby = true;
        private bool showPeers = true;

        private ServerStatusData lastServerStatus;

        [MenuItem("Window/Conduit/Network Monitor")]
        public static void ShowWindow() {
            var window = GetWindow<ConduitMonitorWindow>("Conduit Monitor");
            window.Show();
        }

        private void OnEnable() {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable() {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate() {
            // Limit repaint frequency to avoid Editor GUI lag (~2 fps update is enough for an overview, or 5 fps)
            if (EditorApplication.timeSinceStartup - lastRepaintTime > 0.2f) {
                lastRepaintTime = EditorApplication.timeSinceStartup;
                Repaint();
            }

            if (Application.isPlaying && Conduit.Instance != null) {
                if (EditorApplication.timeSinceStartup - lastRttPollTime > 1.0f) {
                    lastRttPollTime = EditorApplication.timeSinceStartup;
                    PollLatencies(Conduit.Instance);
                }

                if (EditorApplication.timeSinceStartup - lastStatusPollTime > 2.0f) {
                    lastStatusPollTime = EditorApplication.timeSinceStartup;
                    PollServerStatus(Conduit.Instance);
                }
            }
        }

        private async void PollLatencies(Conduit conduit) {
            if (!Application.isPlaying) return;

            // Server Latency Ping (measured via API HTTP request latency)
            if (Conduit.ApiUrl != null) {
                conduit.StartCoroutine(MeasureServerLatency(Conduit.ApiUrl));
            }

            // Peer RTT
            var peerMap = conduit._peerConnectionMap;
            if (peerMap != null) {
                foreach (var kvp in peerMap) {
                    string peerId = kvp.Key;
                    var rtt = await Conduit.GetRTTAsync(peerId);
                    if (rtt.HasValue) {
                        if (!peerRttHistory.ContainsKey(peerId)) peerRttHistory[peerId] = new List<float>();
                        RecordHistory(peerRttHistory[peerId], (float)rtt.Value);
                    }
                }
            }
        }

        private void PollServerStatus(Conduit conduit) {
            if (!Application.isPlaying) return;
            if (Conduit.Config == null) return;

            if (Conduit.ApiUrl != null) {
                string statusUrl = string.Format(Conduit.Config.StatusPath, Conduit.ApiUrl);

                conduit.StartCoroutine(ConduitNet.Http.HttpRequest.Get(statusUrl)
                    .SetTimeout(2)
                    .Send<ServerStatusData>(
                        onSuccess: res => {
                            lastServerStatus = res;
                        },
                        onError: (c, err) => {
                            // Ignored or handled silently
                        }
                    ));
            }
        }

        private IEnumerator MeasureServerLatency(string apiUrl) {
            if (Conduit.Config == null) yield break;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            yield return ConduitNet.Http.HttpRequest.Get(string.Format(Conduit.Config.HealthPath, apiUrl))
                .SetTimeout(2)
                .Send(
                    onSuccess: res => {
                        sw.Stop();
                        RecordHistory(serverLatencyHistory, (float)sw.Elapsed.TotalMilliseconds);
                    },
                    onError: (c, err) => {
                        sw.Stop();
                        // Optional: Handle error or skip recording
                    }
                );
        }

        private void RecordHistory(List<float> list, float value) {
            list.Add(value);
            if (list.Count > MaxHistory) list.RemoveAt(0);
        }

        private void OnGUI() {
            // Increase the default label width to prevent long labels from getting truncated
            float originalLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 220f;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Conduit Network Monitor", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            if (!Application.isPlaying) {
                EditorGUILayout.HelpBox("Conduit Monitor is only active during Play Mode.", MessageType.Info);
                EditorGUIUtility.labelWidth = originalLabelWidth;
                return;
            }

            var conduit = Conduit.Instance;
            if (conduit == null) {
                EditorGUILayout.HelpBox("Conduit Instance not found in the current scene.", MessageType.Warning);
                EditorGUIUtility.labelWidth = originalLabelWidth;
                return;
            }

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
            try {
                DrawSignalingSection(conduit);
                DrawServerStatusSection();
                DrawLobbySection();
                DrawPeersSection(conduit);
            }
            finally {
                EditorGUILayout.EndScrollView();
            }

            // Restore original label width
            EditorGUIUtility.labelWidth = originalLabelWidth;
        }

        private void DrawSignalingSection(Conduit conduit) {
            showSignaling = EditorGUILayout.Foldout(showSignaling, "Signaling Server Status", true, EditorStyles.foldoutHeader);
            if (!showSignaling) return;

            EditorGUI.indentLevel++;

            bool isConnected = Conduit.IsConnectedToServer;
            DrawStatusBadge("Connection", isConnected ? "Connected" : "Disconnected", isConnected ? Color.green : Color.red);

            // Direct access to internal fields
            int incomingCount = conduit._incomingMessageQueue.Count;
            EditorGUILayout.LabelField("Incoming Messages Queue", incomingCount.ToString());

            EditorGUILayout.Space();
            DrawGraph("Signaling Latency", serverLatencyHistory, Color.cyan);

            EditorGUI.indentLevel--;
            EditorGUILayout.Space();
        }

        private void DrawServerStatusSection() {
            showServerStatus = EditorGUILayout.Foldout(showServerStatus, "Server Health & Status", true, EditorStyles.foldoutHeader);
            if (!showServerStatus) return;

            EditorGUI.indentLevel++;

            if (lastServerStatus == null) {
                EditorGUILayout.HelpBox("Waiting for server status...", MessageType.None);
            }
            else {
                var s = lastServerStatus;

                // System Info
                EditorGUILayout.LabelField("System", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("Platform", s.system?.platform ?? "Unknown");
                EditorGUILayout.LabelField("Uptime", s.system != null ? $"{TimeSpan.FromSeconds(s.system.uptimeSeconds):dd\\.hh\\:mm\\:ss}" : "N/A");

                string eventLoopLag = s.system != null ? $"Mean: {s.system.eventLoopLagMeanMs:F2}ms / Max: {s.system.eventLoopLagMaxMs:F2}ms" : "N/A";
                EditorGUILayout.LabelField("Event Loop Lag", eventLoopLag);

                string cores = s.cpu != null ? s.cpu.cores.ToString() : "?";
                string model = s.cpu != null ? s.cpu.model : "Unknown";
                EditorGUILayout.LabelField("CPU", $"{model} ({cores} Cores)");

                string loads = s.cpu?.loadAverage != null && s.cpu.loadAverage.Length >= 3 ? $"{s.cpu.loadAverage[0]:F2}, {s.cpu.loadAverage[1]:F2}, {s.cpu.loadAverage[2]:F2}" : "N/A";
                EditorGUILayout.LabelField("Load Average", loads);
                EditorGUI.indentLevel--;
                EditorGUILayout.Space();

                // Memory Info
                EditorGUILayout.LabelField("Memory", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                if (s.memory != null) {
                    EditorGUILayout.LabelField("System RAM", $"{FormatBytes(s.memory.usedBytes)} / {FormatBytes(s.memory.totalBytes)} ({s.memory.usagePercent:F1}%)");
                    EditorGUILayout.LabelField("Process RSS", FormatBytes(s.memory.processRssBytes));
                    EditorGUILayout.LabelField("Process Heap", $"{FormatBytes(s.memory.processHeapUsedBytes)} / {FormatBytes(s.memory.processHeapTotalBytes)}");
                }
                EditorGUI.indentLevel--;
                EditorGUILayout.Space();

                // Conduit Info
                EditorGUILayout.LabelField("Conduit Engine", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                if (s.websocket != null) {
                    EditorGUILayout.LabelField("Active WS Connections", s.websocket.activeConnections.ToString());
                    EditorGUILayout.LabelField("Sig. Latency avg", $"{s.websocket.averageLatency:F1} ms");
                }
                if (s.lobby != null) {
                    EditorGUILayout.LabelField("Active Lobbies", s.lobby.activeCount.ToString());
                }
                EditorGUI.indentLevel--;
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space();
        }

        private void DrawLobbySection() {
            showLobby = EditorGUILayout.Foldout(showLobby, "Lobby & Local State", true, EditorStyles.foldoutHeader);
            if (!showLobby) return;

            EditorGUI.indentLevel++;

            var localUser = Conduit.LocalUser;
            EditorGUILayout.TextField("Local User ID", localUser != null ? localUser.Id : "None");
            EditorGUILayout.LabelField("Is Host", Conduit.IsHost.ToString());

            var lobby = Conduit.Lobby;
            if (lobby != null) {
                EditorGUILayout.TextField("Lobby ID", lobby.Id);

                var host = Conduit.Host;
                EditorGUILayout.TextField("Host ID", host != null ? host.Id : "None");

                var members = Conduit.Members;
                int memberCount = members != null ? members.Count : 0;
                EditorGUILayout.LabelField("Members Count", memberCount.ToString());

                if (memberCount > 0) {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField("Member List:", EditorStyles.boldLabel);
                    foreach (var member in members) {
                        EditorGUILayout.TextField("Member ID", member.Id);
                    }
                    EditorGUI.indentLevel--;
                }
            }
            else {
                EditorGUILayout.LabelField("Lobby ID", "Not in a lobby");
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space();
        }

        private void DrawPeersSection(Conduit conduit) {
            showPeers = EditorGUILayout.Foldout(showPeers, "WebRTC P2P Connections", true, EditorStyles.foldoutHeader);
            if (!showPeers) return;

            EditorGUI.indentLevel++;

            int queueCount = conduit._dataChannelQueue?.Count ?? 0;
            EditorGUILayout.LabelField("Pending Data Channel Queue", queueCount.ToString());
            string maxDcTime = Conduit.Config != null ? Conduit.Config.MaxDataChannelProcessingTimeMs.ToString() + " ms" : "N/A";
            EditorGUILayout.LabelField("Max DC Processing Time", maxDcTime);

            EditorGUILayout.Space();

            var peerMap = conduit._peerConnectionMap;
            if (peerMap == null || peerMap.Count == 0) {
                EditorGUILayout.HelpBox("No active peer connections.", MessageType.None);
            }
            else {
                EditorGUILayout.LabelField($"Connected Peers ({peerMap.Count})", EditorStyles.boldLabel);
                foreach (var kvp in peerMap) {
                    string peerId = kvp.Key;
                    var rtcConnection = kvp.Value;

                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.TextField("Peer ID:", peerId);

                    if (rtcConnection != null) {
                        var connState = rtcConnection.ConnectionState;
                        var iceState = rtcConnection.IceConnectionState;

                        EditorGUILayout.LabelField("Connection State:", connState.ToString());
                        EditorGUILayout.LabelField("ICE State:", iceState.ToString());

                        EditorGUILayout.Space();
                        if (peerRttHistory.TryGetValue(peerId, out var history)) {
                            DrawGraph("Peer RTT", history, Color.yellow);
                        }
                        else {
                            DrawGraph("Peer RTT", null, Color.yellow);
                        }
                    }
                    else {
                        EditorGUILayout.LabelField("State:", "Unknown (Null)");
                    }

                    EditorGUILayout.EndVertical();
                }
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space();
        }

        // --- Utility Methods --- //

        private string FormatBytes(double bytes) {
            string[] suffix = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double dblSByte = bytes;
            while (dblSByte > 1024 && i < suffix.Length - 1) {
                dblSByte /= 1024;
                i++;
            }
            return $"{dblSByte:0.##} {suffix[i]}";
        }

        private void DrawStatusBadge(string label, string status, Color color) {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth));
            var originalColor = GUI.contentColor;
            GUI.contentColor = color;
            EditorGUILayout.LabelField(status, EditorStyles.boldLabel);
            GUI.contentColor = originalColor;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawGraph(string label, List<float> history, Color color, float min = 0, float defaultMax = 300) {
            if (history == null || history.Count == 0) {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth));
                EditorGUILayout.LabelField("Measuring...", GUILayout.Width(100));
                EditorGUILayout.EndHorizontal();
                return;
            }

            float currentVal = history[^1];
            float maxFound = defaultMax;

            AnimationCurve curve = new();
            for (int i = 0; i < history.Count; i++) {
                curve.AddKey(i, history[i]);
                if (history[i] > maxFound) maxFound = history[i];
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth));
            EditorGUILayout.LabelField($"{currentVal:F0} ms", GUILayout.Width(50));

            GUI.enabled = false;
            var oldColor = GUI.color;
            GUI.color = color;
            // Draw a non-interactable curve field to visualize the rtt array
            EditorGUILayout.CurveField(curve, color, new Rect(0, min, Mathf.Max(history.Count, 1), maxFound * 1.2f), GUILayout.Height(30), GUILayout.ExpandWidth(true));
            GUI.color = oldColor;
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();
        }

        [Serializable]
        private class ServerStatusData {
            public string timestamp { get; set; }
            public CpuStatus cpu { get; set; }
            public MemoryStatus memory { get; set; }
            public WebSocketStatus websocket { get; set; }
            public LobbyStatus lobby { get; set; }
            public SystemStatus system { get; set; }
        }

        [Serializable]
        private class CpuStatus {
            public float[] loadAverage { get; set; }
            public int cores { get; set; }
            public string model { get; set; }
        }

        [Serializable]
        private class MemoryStatus {
            public double totalBytes { get; set; }
            public double freeBytes { get; set; }
            public double usedBytes { get; set; }
            public float usagePercent { get; set; }
            public double processHeapTotalBytes { get; set; }
            public double processHeapUsedBytes { get; set; }
            public double processRssBytes { get; set; }
        }

        [Serializable]
        private class WebSocketStatus {
            public int activeConnections { get; set; }
            public float averageLatency { get; set; }
        }

        [Serializable]
        private class LobbyStatus {
            public int activeCount { get; set; }
        }

        [Serializable]
        private class SystemStatus {
            public double uptimeSeconds { get; set; }
            public string platform { get; set; }
            public float eventLoopLagMeanMs { get; set; }
            public float eventLoopLagMaxMs { get; set; }
        }
    }
}
