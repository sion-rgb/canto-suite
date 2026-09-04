import 'dart:io';

import 'package:canto_meet/core/meeting_store.dart';
import 'package:canto_meet/main.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

void main() {
  test('relaunch recognizes installed ASR directory and LLM file', () async {
    final root = await Directory.systemTemp.createTemp('canto-model-state-');
    try {
      final asr = await Directory('${root.path}/asr').create();
      final llm = await File('${root.path}/model.gguf').writeAsBytes([1]);
      SharedPreferences.setMockInitialValues({
        'quality_profile': 'standard',
        'asr_model_path': asr.path,
        'meeting_llm_path': llm.path,
      });
      final preferences = await SharedPreferences.getInstance();
      expect(hasInstalledMobileModels(preferences), isTrue);
    } finally {
      await root.delete(recursive: true);
    }
  });

  testWidgets('first launch explains local privacy and quality',
      (tester) async {
    SharedPreferences.setMockInitialValues({});
    final preferences = await SharedPreferences.getInstance();
    await tester.pumpWidget(
        CantoMeetApp(store: MeetingStore(), preferences: preferences));
    expect(find.text('選擇 AI 品質'), findsOneWidget);
    expect(find.textContaining('不會上載錄音'), findsOneWidget);
    expect(find.text('標準'), findsOneWidget);
  });

  testWidgets('output setting persists simplified Chinese selection',
      (tester) async {
    SharedPreferences.setMockInitialValues({'output_script': 'traditional'});
    final preferences = await SharedPreferences.getInstance();
    await tester.pumpWidget(
        MaterialApp(home: OutputSettingsScreen(preferences: preferences)));

    await tester.tap(find.text('簡體中文'));
    await tester.pump();

    expect(preferences.getString('output_script'), 'simplified');
  });
}
