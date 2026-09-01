enum MeetingState {
  recording,
  saved,
  transcribing,
  summarizing,
  complete,
  failed
}

class Meeting {
  const Meeting(
      {required this.id,
      required this.title,
      required this.createdAt,
      required this.state,
      this.durationSeconds = 0});
  final String id;
  final String title;
  final DateTime createdAt;
  final MeetingState state;
  final int durationSeconds;
}

class TranscriptSegment {
  const TranscriptSegment({
    required this.id,
    required this.meetingId,
    required this.sequence,
    required this.audioPath,
    this.rawText,
    this.cleanedText,
    this.userText,
    this.speakerId,
    required this.state,
  });
  final String id;
  final String meetingId;
  final int sequence;
  final String audioPath;
  final String? rawText;
  final String? cleanedText;
  final String? userText;
  final String? speakerId;
  final String state;

  String get displayText => userText ?? cleanedText ?? rawText ?? '';
}

class ActionItem {
  const ActionItem(
      {required this.id,
      required this.text,
      this.owner,
      this.dueDate,
      this.done = false});
  final String id;
  final String text;
  final String? owner;
  final String? dueDate;
  final bool done;

  Map<String, dynamic> toJson() => {
        'id': id,
        'text': text,
        'owner': owner,
        'dueDate': dueDate,
        'done': done,
      };
}

class MeetingReport {
  const MeetingReport({
    required this.summary,
    required this.topics,
    required this.decisions,
    required this.actionItems,
    required this.followUps,
    required this.unresolvedQuestions,
    required this.risks,
  });

  final String summary;
  final List<String> topics;
  final List<String> decisions;
  final List<ActionItem> actionItems;
  final List<String> followUps;
  final List<String> unresolvedQuestions;
  final List<String> risks;

  factory MeetingReport.empty() => const MeetingReport(
      summary: '',
      topics: [],
      decisions: [],
      actionItems: [],
      followUps: [],
      unresolvedQuestions: [],
      risks: []);

  factory MeetingReport.fromJson(Map<String, dynamic> json) {
    const unknownValues = {'null', 'unknown', '未指定', '未指定。'};
    List<String> strings(String key) => (json[key] as List<dynamic>? ??
            const [])
        .map((value) => value.toString().trim())
        .where((value) =>
            value.isNotEmpty && !unknownValues.contains(value.toLowerCase()))
        .toList();
    final rawActions = json['actionItems'] as List<dynamic>? ?? const [];
    final actions = <ActionItem>[];
    for (var index = 0; index < rawActions.length; index++) {
      final raw = rawActions[index];
      if (raw is! Map) continue;
      final text = raw['text']?.toString().trim() ?? '';
      if (text.isEmpty || unknownValues.contains(text.toLowerCase())) continue;
      String? nullable(Object? value) {
        final text = value?.toString().trim();
        if (text == null ||
            text.isEmpty ||
            unknownValues.contains(text.toLowerCase())) {
          return null;
        }
        return text;
      }

      actions.add(ActionItem(
          id: index.toString(),
          text: text,
          owner: nullable(raw['owner']),
          dueDate: nullable(raw['dueDate'])));
    }
    return MeetingReport(
      summary: json['summary']?.toString().trim() ?? '',
      topics: strings('topics'),
      decisions: strings('decisions'),
      actionItems: actions,
      followUps: strings('followUps'),
      unresolvedQuestions: strings('unresolvedQuestions'),
      risks: strings('risks'),
    );
  }

  Map<String, dynamic> toJson() => {
        'summary': summary,
        'topics': topics,
        'decisions': decisions,
        'actionItems': actionItems.map((item) => item.toJson()).toList(),
        'followUps': followUps,
        'unresolvedQuestions': unresolvedQuestions,
        'risks': risks,
      };
}
