import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'core/model_download.dart';
import 'core/model_registry.dart';

class AiModelsScreen extends StatefulWidget {
  const AiModelsScreen({super.key, this.onReady});
  final VoidCallback? onReady;
  @override
  State<AiModelsScreen> createState() => _AiModelsScreenState();
}

class _AiModelsScreenState extends State<AiModelsScreen> {
  MobileModelRegistry? registry;
  final Map<String, bool> installed = {};
  bool busy = true;
  String status = '正在讀取及驗證模型…';
  String? details;
  double? progress;
  int storage = 0;
  String hardware = '正在偵測裝置…';
  String recommendation = 'light';
  SharedPreferences? preferences;
  @override
  void initState() {
    super.initState();
    _open();
  }

  Future<void> _open() async {
    try {
      registry = await MobileModelRegistry.open();
      if (widget.onReady != null) {
        preferences = await SharedPreferences.getInstance();
        try {
          final info =
              await const MethodChannel('hk.canto.canto_meet/audio_control')
                  .invokeMapMethod<String, dynamic>('hardwareProfile');
          hardware =
              '${info?['processors']} 核心 · 裝置記憶體 ${info?['memoryMiB']} MiB';
          recommendation = info?['recommendation']?.toString() ?? 'light';
        } catch (_) {
          hardware = '未能讀取硬件資料 · 請按裝置選擇模型';
        }
      }
      await _refresh();
    } catch (error) {
      _error(error);
    }
  }

  Future<void> _refresh() async {
    final current = registry!;
    for (final model in current.models) {
      installed[model.id] = await current.installed(model);
    }
    storage = await current.storageBytes();
    if (mounted) {
      setState(() {
        busy = false;
        progress = null;
        status = '';
      });
    }
  }

  void _error(Object error) {
    if (!mounted) return;
    setState(() {
      busy = false;
      progress = null;
      status = error is ModelSelectionException
          ? error.message
          : error is ModelInstallException
              ? error.userMessage
              : '模型操作未完成。請檢查儲存空間後重試。';
      details = error.toString();
    });
  }

  Future<void> _run(Future<void> Function() action) async {
    setState(() {
      busy = true;
      details = null;
      status = '正在處理…';
    });
    try {
      await action();
      // Display the committed role IDs immediately, before rechecking large files.
      if (mounted) setState(() {});
      await _refresh();
    } catch (error) {
      await _refresh();
      _error(error);
    }
  }

  Future<void> _download(MobileModel model, {bool redownload = false}) =>
      registry!.install(model, redownload: redownload,
          onProgress: (received, total) {
        if (mounted) {
          setState(() {
            progress = received / total;
          });
        }
      }, onSourceChanged: (source) {
        if (mounted) {
          setState(() {
            status = '${model.name} · $source';
          });
        }
      });
  Future<void> _uninstall(MobileModel model) async {
    final confirmed = await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
                title: const Text('卸載模型？'),
                content:
                    Text('${model.name}\n${model.id}\n會刪除模型檔案並即時回收空間；之後可重新下載。'),
                actions: [
                  TextButton(
                      onPressed: () => Navigator.pop(context, false),
                      child: const Text('取消')),
                  FilledButton(
                      onPressed: () => Navigator.pop(context, true),
                      child: const Text('卸載'))
                ]));
    if (confirmed != true || !mounted) return;
    var reclaimed = 0;
    await _run(() async {
      reclaimed = await registry!.uninstall(model);
    });
    if (mounted && details == null) {
      setState(() {
        status = '已卸載，回收 ${_size(reclaimed)}';
      });
    }
  }

  String _size(int bytes) => '${(bytes / 1024 / 1024).toStringAsFixed(1)} MiB';
  String _roleName(String role) => switch (role) {
        'LIVE_ASR' => '即時轉錄 · LIVE_ASR',
        'QUALITY_ASR' => '正式轉錄 · QUALITY_ASR',
        _ => '會議摘要 · MEETING_LLM'
      };
  @override
  Widget build(BuildContext context) {
    final current = registry;
    return PopScope(
        canPop: !busy,
        child: Scaffold(
            appBar: AppBar(title: const Text('AI 模型')),
            body: ListView(padding: const EdgeInsets.all(16), children: [
              const Text('模型下載後全程離線推理。三個角色可獨立選擇；預設只會選擇一組模型。'),
              const Text('網絡只用於下載模型，不會上載錄音、逐字稿或摘要。'),
              if (widget.onReady != null) ...[
                Text('$hardware · 建議預設：$recommendation（可自行選擇，不會自動更改已選模型）'),
                const SizedBox(height: 12),
                DropdownButtonFormField<String>(
                    key: ValueKey(preferences?.getString('output_script')),
                    initialValue: preferences?.getString('output_script') ??
                        'traditional',
                    decoration: const InputDecoration(labelText: '輸出中文格式'),
                    items: const [
                      DropdownMenuItem(
                          value: 'traditional', child: Text('香港繁體（預設）')),
                      DropdownMenuItem(
                          value: 'simplified', child: Text('簡體中文')),
                    ],
                    onChanged: busy
                        ? null
                        : (value) async {
                            await preferences?.setString(
                                'output_script', value!);
                          }),
              ],
              if (current != null) ...[
                const SizedBox(height: 12),
                Text('模型儲存空間（包括續傳暫存）：${_size(storage)}'),
                for (final role in mobileRoles)
                  Padding(
                      padding: const EdgeInsets.symmetric(vertical: 8),
                      child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            DropdownButtonFormField<String>(
                                key: ValueKey(
                                    '$role-${current.selections[role]}'),
                                isExpanded: true,
                                initialValue: current.selections[role],
                                decoration: InputDecoration(
                                    labelText: _roleName(role),
                                    border: const OutlineInputBorder()),
                                items: current.models
                                    .where(
                                        (model) => model.roles.contains(role))
                                    .map((model) => DropdownMenuItem(
                                        value: model.id,
                                        child: Text(model.name,
                                            overflow: TextOverflow.ellipsis)))
                                    .toList(),
                                onChanged: busy
                                    ? null
                                    : (id) => _run(
                                        () => current.setActive(role, id!))),
                            Text(current.selections[role]!,
                                style: Theme.of(context).textTheme.bodySmall),
                            Text(installed[current.selections[role]] == true
                                ? '已安裝 · 已選用'
                                : '未安裝或需修復 · 此功能暫停，請先下載所選模型'),
                          ])),
                ExpansionTile(
                    title: Text('可選預設組合 · ${current.preset}'),
                    children: [
                      for (final entry in mobilePresets.entries)
                        ListTile(
                            title: Text(switch (entry.key) {
                              'light' => 'Low · 輕量',
                              'standard' => 'Standard · 標準',
                              _ => 'High · 高規格'
                            }),
                            subtitle: Text(entry.value.entries
                                .map((role) => '${role.key}: ${role.value}')
                                .join('\n')),
                            trailing: TextButton(
                                onPressed: busy
                                    ? null
                                    : () => _run(
                                        () => current.applyPreset(entry.key)),
                                child: const Text('套用'))),
                    ]),
                FilledButton(
                    onPressed: busy
                        ? null
                        : () => _run(() async {
                              for (final id
                                  in current.selections.values.toSet()) {
                                await _download(current.byId(id));
                              }
                            }),
                    child: const Text('下載／修復所有已選模型')),
                if (widget.onReady != null)
                  FilledButton(
                      onPressed: busy ||
                              current.selections.values
                                  .any((id) => installed[id] != true)
                          ? null
                          : widget.onReady,
                      child: const Text('完成設定並繼續')),
                const Divider(height: 24),
                const Text('所有可用模型 · 選擇前可先下載'),
                for (final model in current.models)
                  Card(
                      margin: const EdgeInsets.symmetric(vertical: 8),
                      child: Padding(
                          padding: const EdgeInsets.all(12),
                          child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Text(model.name,
                                    style: Theme.of(context)
                                        .textTheme
                                        .titleMedium),
                                SelectableText(model.id),
                                Text(
                                    '${model.roles.where(mobileRoles.contains).join(' / ')}\n版本 ${model.revision}\n${_size(model.bytes)}'),
                                Text(
                                    '${installed[model.id] == true ? '已安裝及 SHA-256 驗證' : '未安裝或需修復'} · ${current.isSelected(model.id) ? '已選用' : '未啟用'}${current.inUse(model.id) ? ' · 使用中' : ''}'),
                                if (model.id == qwenAsrId)
                                  const Text('離線分段模型；即時模式延遲及記憶體用量較高，請按裝置測試。'),
                                Wrap(spacing: 8, children: [
                                  TextButton(
                                      onPressed: busy
                                          ? null
                                          : () => _run(() => _download(model)),
                                      child: Text(installed[model.id] == true
                                          ? '驗證／修復'
                                          : '下載並安裝')),
                                  TextButton(
                                      onPressed: busy
                                          ? null
                                          : () => _run(() => _download(model,
                                              redownload: true)),
                                      child: const Text('重新下載')),
                                  if (installed[model.id] == true)
                                    TextButton(
                                        onPressed: busy
                                            ? null
                                            : () => _uninstall(model),
                                        child: const Text('卸載／刪除')),
                                ]),
                              ]))),
              ],
              if (busy) LinearProgressIndicator(value: progress),
              if (status.isNotEmpty)
                Padding(
                    padding: const EdgeInsets.symmetric(vertical: 12),
                    child: Text(status)),
              if (details != null) ...[
                OutlinedButton(
                    onPressed: busy ? null : _open, child: const Text('重試檢查')),
                ExpansionTile(
                    title: const Text('進階資料'),
                    children: [SelectableText(details!)])
              ],
            ])));
  }
}
