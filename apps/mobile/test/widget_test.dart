import 'package:canto_meet/main.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

void main() {
  testWidgets(
      'settings exposes independent AI model management and persists script',
      (tester) async {
    SharedPreferences.setMockInitialValues({'output_script': 'traditional'});
    final preferences = await SharedPreferences.getInstance();
    await tester.pumpWidget(
        MaterialApp(home: OutputSettingsScreen(preferences: preferences)));
    expect(find.text('AI 模型'), findsOneWidget);
    expect(find.textContaining('獨立選擇'), findsOneWidget);
    await tester.tap(find.text('簡體中文'));
    await tester.pump();
    expect(preferences.getString('output_script'), 'simplified');
  });
}
