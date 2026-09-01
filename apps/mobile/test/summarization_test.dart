import 'dart:convert';

import 'package:canto_meet/core/local_meeting_llm.dart';
import 'package:canto_meet/core/models.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('hierarchical summarization stays bounded and preserves unknown fields',
      () async {
    final prompts = <String>[];
    Future<String> fake(String prompt, {int maxTokens = 1024}) async {
      prompts.add(prompt);
      return jsonEncode({
        'summary': '本地摘要',
        'topics': ['產品'],
        'decisions': [],
        'actionItems': [
          {'text': '跟進設計', 'owner': null, 'dueDate': null}
        ],
        'followUps': [],
        'unresolvedQuestions': ['尚未決定'],
        'risks': []
      });
    }

    final segments = List.generate(
        12,
        (index) => TranscriptSegment(
            id: '$index',
            meetingId: 'meeting',
            sequence: index,
            audioPath: 'segment-$index.m4a',
            cleanedText: '第 $index 段內容：${List.filled(8, '討論產品方向。').join()}',
            state: 'quality_complete'));
    final report = await HierarchicalMeetingSummarizer(fake,
            maxChunkCharacters: 180, fanIn: 3)
        .summarize(segments, simplified: false);

    expect(prompts.length, greaterThan(3));
    expect(prompts.where((prompt) => prompt.contains('抽取事實')).length,
        greaterThan(1));
    expect(report.actionItems.single.owner, isNull);
    expect(report.actionItems.single.dueDate, isNull);
    expect(report.summary, '本地摘要');
  });
}
