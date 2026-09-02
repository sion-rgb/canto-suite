package hk.canto.canto_meet

import android.content.res.AssetManager
import org.json.JSONObject

internal class HongKongChineseConverter(private val assets: AssetManager) {
    private val stages: List<OpenCcStage> by lazy {
        listOf(
            OpenCcStage.load(assets, "opencc/STPhrases.txt", "opencc/STCharacters.txt"),
            OpenCcStage.load(assets, "opencc/HKVariantsPhrases.txt", "opencc/HKVariants.txt"),
        )
    }

    fun convert(text: String): String = stages.fold(text) { value, stage -> stage.convert(value) }

    private class OpenCcStage(
        private val mappings: Map<String, String>,
        private val maximumKeyLength: Int,
    ) {
        fun convert(text: String): String {
            val output = StringBuilder(text.length)
            var index = 0
            while (index < text.length) {
                var replacement: String? = null
                var consumed = 1
                var length = minOf(maximumKeyLength, text.length - index)
                while (length > 0) {
                    replacement = mappings[text.substring(index, index + length)]
                    if (replacement != null) {
                        consumed = length
                        break
                    }
                    length--
                }
                output.append(replacement ?: text[index])
                index += consumed
            }
            return output.toString()
        }

        companion object {
            fun load(assets: AssetManager, vararg paths: String): OpenCcStage {
                val mappings = linkedMapOf<String, String>()
                var maximum = 1
                paths.forEach { path ->
                    assets.open(path).bufferedReader(Charsets.UTF_8).useLines { lines ->
                        lines.forEach { line ->
                            if (line.isBlank() || line.startsWith("#")) return@forEach
                            val tab = line.indexOf('\t')
                            if (tab <= 0 || tab == line.lastIndex) return@forEach
                            val source = line.substring(0, tab)
                            val alternatives = line.substring(tab + 1)
                            val target = alternatives.substringBefore(' ')
                            mappings.putIfAbsent(source, target)
                            maximum = maxOf(maximum, source.length)
                        }
                    }
                }
                return OpenCcStage(mappings, maximum)
            }
        }
    }
}

internal class CantoneseCleaner(private val assets: AssetManager) {
    private val terminology: List<Pair<Regex, String>> by lazy {
        val root = JSONObject(
            assets.open("cantonese-dictionary/default.v1.json")
                .bufferedReader(Charsets.UTF_8).use { it.readText() },
        )
        val entries = root.getJSONArray("entries")
        buildList {
            for (index in 0 until entries.length()) {
                val entry = entries.getJSONObject(index)
                val spoken = entry.getString("spoken")
                val options = if (entry.optBoolean("caseSensitive", false)) emptySet() else setOf(RegexOption.IGNORE_CASE)
                add(Regex(Regex.escape(spoken), options) to entry.getString("display"))
            }
        }.sortedByDescending { it.first.pattern.length }
    }

    fun clean(text: String): String {
        var output = text.trim()
        terminology.forEach { (pattern, display) -> output = pattern.replace(output, display) }
        listOf("呃", "嗯", "哦").forEach { filler ->
            output = Regex("(?:${Regex.escape(filler)}[\\s，,、]*){3,}").replace(output, filler)
        }
        listOf("即係", "其實", "我哋", "咁樣").forEach { restart ->
            output = Regex("${Regex.escape(restart)}(?:[\\s，,、]+${Regex.escape(restart)})+")
                .replace(output, restart)
        }
        output = output.replace(Regex("[，,、]{2,}"), "，")
            .replace(Regex("\\s+([，。！？；：])"), "$1")
            .replace(Regex("([，。！？；：])\\s+"), "$1")
        if (Regex("[\\u3400-\\u9fff]").containsMatchIn(output) &&
            !Regex("[。！？….!?]$").containsMatchIn(output)
        ) output += "。"
        return output
    }
}
