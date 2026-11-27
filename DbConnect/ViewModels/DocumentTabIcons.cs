using System;
using System.Collections.Generic;
using FluentIcons.Common;

namespace DbConnect.ViewModels;

/// <summary>
/// The one Fluent icon each document tab type shows in the tab strip. Tab titles carry no glyphs; the icon tells
/// the tab types apart. Query, table-data, procedure and Find in Data tabs override <see cref="ViewModelBase.TabIcon"/>.
/// </summary>
public static class DocumentTabIcons
{
    private static readonly Dictionary<Type, Symbol> Icons = new()
    {
        [typeof(TableDesignerViewModel)] = Symbol.TableEdit,
        [typeof(IndexManagerViewModel)] = Symbol.Flash,
        [typeof(ViewDesignerViewModel)] = Symbol.Eye,
        [typeof(DependencyAnalysisViewModel)] = Symbol.BranchFork,
        [typeof(SchemaDiffViewModel)] = Symbol.BranchCompare,
        [typeof(SchemaHistoryViewModel)] = Symbol.History,
        [typeof(AuditLogViewModel)] = Symbol.DocumentBulletList,
        [typeof(DataCompareViewModel)] = Symbol.TableSwitch,
        [typeof(DataTransferViewModel)] = Symbol.ArrowSwap,
        [typeof(BackupRestoreViewModel)] = Symbol.Archive,
        [typeof(AutomationViewModel)] = Symbol.CalendarClock,
        [typeof(UserManagementViewModel)] = Symbol.People,
        [typeof(QueryBuilderViewModel)] = Symbol.PuzzlePiece,
        [typeof(MaskingRulesViewModel)] = Symbol.EyeOff,
        [typeof(ErDiagramViewModel)] = Symbol.Organization,
        [typeof(ConnectionMonitorViewModel)] = Symbol.Pulse,
        [typeof(SlowQueryDashboardViewModel)] = Symbol.Gauge,
    };

    public static Symbol? For(Type tabType) => Icons.TryGetValue(tabType, out var icon) ? icon : null;
}
