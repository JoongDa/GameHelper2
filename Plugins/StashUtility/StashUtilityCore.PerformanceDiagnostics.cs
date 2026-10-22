// <copyright file="StashUtilityCore.PerformanceDiagnostics.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace StashUtility
{
    using System;
    using System.Diagnostics;
    using ImGuiNET;

    public sealed partial class StashUtilityCore
    {
        private bool equipmentDiagnosticsEnabled;
        private bool equipmentPerformanceFrameActive;
        private EquipmentPerformanceCounters equipmentPerformance;
        private EquipmentPerformanceCounters equipmentPerformanceWindowTotals;
        private EquipmentPerformanceCounters equipmentPerformanceWindowPeaks;
        private EquipmentPerformanceCounters equipmentPerformanceLastTotals;
        private EquipmentPerformanceCounters equipmentPerformanceLastPeaks;
        private long equipmentPerformanceFrameStartedAt;
        private long equipmentPerformanceFrameAllocatedAt;
        private long equipmentPerformanceWindowStartedAt;
        private long equipmentPerformanceWindowAllocated;
        private long equipmentPerformanceWindowAllocationPeak;
        private long equipmentPerformanceLastAllocated;
        private long equipmentPerformanceLastAllocationPeak;
        private int equipmentPerformanceWindowFrames;
        private int equipmentPerformanceLastFrames;
        private double equipmentPerformanceWindowMilliseconds;
        private double equipmentPerformanceWindowTimePeak;
        private double equipmentPerformanceLastMilliseconds;
        private double equipmentPerformanceLastTimePeak;
        private double equipmentPerformanceLastSeconds;

        private struct EquipmentPerformanceCounters
        {
            internal int MemoryCalls;
            internal int Vectors;
            internal int Items;
            internal int UiElements;
            internal int MetadataValidations;
            internal int Refreshes;
            internal int Deferred;
            internal int ReadFailures;
            internal int RuleCompiles;
            internal int SettingsWrites;

            internal void Add(EquipmentPerformanceCounters value)
            {
                MemoryCalls += value.MemoryCalls;
                Vectors += value.Vectors;
                Items += value.Items;
                UiElements += value.UiElements;
                MetadataValidations += value.MetadataValidations;
                Refreshes += value.Refreshes;
                Deferred += value.Deferred;
                ReadFailures += value.ReadFailures;
                RuleCompiles += value.RuleCompiles;
                SettingsWrites += value.SettingsWrites;
            }

            internal void Maximize(EquipmentPerformanceCounters value)
            {
                MemoryCalls = Math.Max(MemoryCalls, value.MemoryCalls);
                Vectors = Math.Max(Vectors, value.Vectors);
                Items = Math.Max(Items, value.Items);
                UiElements = Math.Max(UiElements, value.UiElements);
                MetadataValidations = Math.Max(MetadataValidations, value.MetadataValidations);
                Refreshes = Math.Max(Refreshes, value.Refreshes);
                Deferred = Math.Max(Deferred, value.Deferred);
                ReadFailures = Math.Max(ReadFailures, value.ReadFailures);
                RuleCompiles = Math.Max(RuleCompiles, value.RuleCompiles);
                SettingsWrites = Math.Max(SettingsWrites, value.SettingsWrites);
            }
        }

        private void BeginEquipmentPerformanceFrame()
        {
            if (!equipmentDiagnosticsEnabled) return;
            equipmentPerformanceFrameStartedAt = Stopwatch.GetTimestamp();
            equipmentPerformanceFrameAllocatedAt = GC.GetAllocatedBytesForCurrentThread();
            if (equipmentPerformanceWindowStartedAt == 0)
                equipmentPerformanceWindowStartedAt = equipmentPerformanceFrameStartedAt;
            equipmentPerformanceFrameActive = true;
        }

        private void EndEquipmentPerformanceFrame()
        {
            if (!equipmentPerformanceFrameActive) return;
            long finishedAt = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - equipmentPerformanceFrameAllocatedAt;
            double milliseconds = Stopwatch.GetElapsedTime(equipmentPerformanceFrameStartedAt, finishedAt).TotalMilliseconds;
            equipmentPerformanceFrameActive = false;
            equipmentPerformanceWindowFrames++;
            equipmentPerformanceWindowMilliseconds += milliseconds;
            equipmentPerformanceWindowTimePeak = Math.Max(equipmentPerformanceWindowTimePeak, milliseconds);
            equipmentPerformanceWindowAllocated += allocated;
            equipmentPerformanceWindowAllocationPeak = Math.Max(equipmentPerformanceWindowAllocationPeak, allocated);
            equipmentPerformanceWindowTotals.Add(equipmentPerformance);
            equipmentPerformanceWindowPeaks.Maximize(equipmentPerformance);
            // Include settings writes and rule compiles between DrawUI calls in the next frame's counters.
            equipmentPerformance = default;

            double seconds = Stopwatch.GetElapsedTime(equipmentPerformanceWindowStartedAt, finishedAt).TotalSeconds;
            if (seconds < 1) return;
            equipmentPerformanceLastFrames = equipmentPerformanceWindowFrames;
            equipmentPerformanceLastSeconds = seconds;
            equipmentPerformanceLastMilliseconds = equipmentPerformanceWindowMilliseconds;
            equipmentPerformanceLastTimePeak = equipmentPerformanceWindowTimePeak;
            equipmentPerformanceLastAllocated = equipmentPerformanceWindowAllocated;
            equipmentPerformanceLastAllocationPeak = equipmentPerformanceWindowAllocationPeak;
            equipmentPerformanceLastTotals = equipmentPerformanceWindowTotals;
            equipmentPerformanceLastPeaks = equipmentPerformanceWindowPeaks;
            equipmentPerformanceWindowStartedAt = finishedAt;
            equipmentPerformanceWindowFrames = 0;
            equipmentPerformanceWindowMilliseconds = 0;
            equipmentPerformanceWindowTimePeak = 0;
            equipmentPerformanceWindowAllocated = 0;
            equipmentPerformanceWindowAllocationPeak = 0;
            equipmentPerformanceWindowTotals = default;
            equipmentPerformanceWindowPeaks = default;
        }

        private void DrawEquipmentPerformanceSettings()
        {
            if (ImGui.Checkbox(PluginText.T("stashutility.performance.enabled", "Measure Overlay Performance"), ref equipmentDiagnosticsEnabled))
            {
                equipmentPerformanceFrameActive = false;
                equipmentPerformance = default;
                equipmentPerformanceWindowTotals = default;
                equipmentPerformanceWindowPeaks = default;
                equipmentPerformanceWindowStartedAt = 0;
                equipmentPerformanceWindowFrames = 0;
                equipmentPerformanceWindowMilliseconds = 0;
                equipmentPerformanceWindowTimePeak = 0;
                equipmentPerformanceWindowAllocated = 0;
                equipmentPerformanceWindowAllocationPeak = 0;
                equipmentPerformanceLastFrames = 0;
            }
            if (!equipmentDiagnosticsEnabled) return;

            ImGui.TextWrapped(PluginText.T("stashutility.performance.scope", "DrawUI timing and allocations cover this plugin's overlay thread only. Counters cover plugin entry points; Core-internal reads and parallel parent-cache work are excluded. Disable Debug Settings for normal cache measurements."));
            if (equipmentPerformanceLastFrames == 0)
            {
                ImGui.TextDisabled(PluginText.T("stashutility.performance.waiting", "Collecting a one-second sample..."));
                return;
            }

            ImGui.TextUnformatted(PluginText.F("stashutility.performance.window", "Last window: {0} frames / {1:F2} s", equipmentPerformanceLastFrames, equipmentPerformanceLastSeconds));
            if (!ImGui.BeginTable("EquipmentPerformanceCounters", 4, ImGuiTableFlags.BordersInnerH)) return;
            ImGui.TableSetupColumn(PluginText.T("stashutility.performance.metric", "Metric"));
            ImGui.TableSetupColumn(PluginText.T("stashutility.performance.average", "Average / frame"));
            ImGui.TableSetupColumn(PluginText.T("stashutility.performance.peak", "Peak / frame"));
            ImGui.TableSetupColumn(PluginText.T("stashutility.performance.total", "Window total"));
            ImGui.TableHeadersRow();
            DrawEquipmentPerformanceRow(PluginText.T("stashutility.performance.time", "DrawUI (ms)"), equipmentPerformanceLastMilliseconds, equipmentPerformanceLastTimePeak, "F3");
            DrawEquipmentPerformanceRow(PluginText.T("stashutility.performance.allocated", "Allocated (bytes)"), equipmentPerformanceLastAllocated, equipmentPerformanceLastAllocationPeak, "N0");
            var totals = equipmentPerformanceLastTotals;
            var peaks = equipmentPerformanceLastPeaks;
            DrawEquipmentPerformanceRow(PluginText.T("stashutility.performance.memory", "Memory read calls"), totals.MemoryCalls, peaks.MemoryCalls);
            DrawEquipmentPerformanceRow(PluginText.T("stashutility.performance.vectors", "Vector reads"), totals.Vectors, peaks.Vectors);
            DrawEquipmentPerformanceRow(PluginText.T("stashutility.performance.items", "Items created"), totals.Items, peaks.Items);
            DrawEquipmentPerformanceRow(PluginText.T("stashutility.performance.elements", "UI elements created"), totals.UiElements, peaks.UiElements);
            DrawEquipmentPerformanceRow(PluginText.T("stashutility.performance.metadata", "Full Metadata validations"), totals.MetadataValidations, peaks.MetadataValidations);
            DrawEquipmentPerformanceRow(PluginText.T("stashutility.performance.refreshes", "Snapshot refreshes"), totals.Refreshes, peaks.Refreshes);
            DrawEquipmentPerformanceRow(PluginText.T("stashutility.performance.deferred", "Deferred refreshes"), totals.Deferred, peaks.Deferred);
            DrawEquipmentPerformanceRow(PluginText.T("stashutility.performance.failures", "Snapshot read failures"), totals.ReadFailures, peaks.ReadFailures);
            DrawEquipmentPerformanceRow(PluginText.T("stashutility.performance.compiles", "Category rule compiles"), totals.RuleCompiles, peaks.RuleCompiles);
            DrawEquipmentPerformanceRow(PluginText.T("stashutility.performance.writes", "Settings file writes"), totals.SettingsWrites, peaks.SettingsWrites);
            ImGui.EndTable();
        }

        private void DrawEquipmentPerformanceRow(string label, double total, double peak, string format = "N0")
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(label);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((total / equipmentPerformanceLastFrames).ToString(format == "N0" ? "N2" : format));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(peak.ToString(format));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(total.ToString(format));
        }
    }
}
