// SPDX-License-Identifier: AGPL-3.0-only
package com.couchsubs.mobile.core

import org.json.JSONArray
import org.json.JSONObject

/** Alanların ölçüldüğü görüntü: konsolun kendi akışı (PS5/Xbox) ya da yakalanan pencere (telefonda yalnız akış). */
enum class AreaSource { STREAM, WINDOW }

/** Bir oyunun alanları: altyazı alanı, hazır seçimi ve ek alanlar. */
data class AreaSet(val region: CropRegion, val areaPreset: AreaPreset, val extraAreas: List<ExtraArea>) {
    fun toJson(): JSONObject = JSONObject()
        .put("region", region.toJson())
        .put("areaPreset", areaPreset.name)
        .put("extraAreas", JSONArray(ExtraArea.listToJson(extraAreas)))

    companion object {
        fun fromJson(json: JSONObject?): AreaSet? {
            json ?: return null
            val region = cropRegionFrom(json.optJSONObject("region")) ?: return null
            val preset = runCatching { AreaPreset.valueOf(json.optString("areaPreset")) }.getOrNull() ?: AreaPreset.matching(region)
            return AreaSet(region, preset, ExtraArea.listFromJson(json.optJSONArray("extraAreas")?.toString()))
        }
    }
}

/** Oyuna göre kaydedilmiş alanlar. [key]: [AreaProfiles.key]; [game]: gösterilen ad. */
data class AreaProfile(val key: String, val game: String, val areas: AreaSet) {
    fun toJson(): JSONObject = JSONObject().put("key", key).put("game", game).put("areas", areas.toJson())

    companion object {
        fun listToJson(profiles: List<AreaProfile>): String = JSONArray().also { array -> profiles.forEach { array.put(it.toJson()) } }.toString()

        fun listFromJson(text: String?): List<AreaProfile> = try {
            val array = JSONArray(text ?: "[]")
            (0 until array.length()).mapNotNull { index ->
                val item = array.optJSONObject(index) ?: return@mapNotNull null
                val key = item.optString("key").takeIf { it.isNotEmpty() } ?: return@mapNotNull null
                val areas = AreaSet.fromJson(item.optJSONObject("areas")) ?: return@mapNotNull null
                AreaProfile(key, item.optString("game", key), areas)
            }.takeLast(AreaProfiles.MAX_COUNT)
        } catch (_: Exception) {
            emptyList()
        }
    }
}

/**
 * Oyuna göre okuma alanları: kullanıcı bir oyunun altyazı alanını ve ek alanlarını kaydeder; o oyun yeniden oynanınca
 * alanlar kendiliğinden gelir. Profil oyun adıyla ([GameName.key]) ve görüntünün kaynağıyla anılır. Windows'taki
 * CouchSubs.Core/AreaProfiles.cs ve Mac/iPhone'daki AreaProfiles.swift ile aynı kurallar.
 */
object AreaProfiles {
    /** En çok bu kadar profil saklanır; fazlasında en uzun süredir kullanılmayan silinir. */
    const val MAX_COUNT = 20

    /** Profilin anahtarı ("far cry 3|stream"); oyun adı boşsa null. */
    fun key(game: String?, source: AreaSource): String? {
        val name = GameName.key(game ?: "")
        return if (name.isEmpty()) null else name + "|" + if (source == AreaSource.WINDOW) "window" else "stream"
    }

    sealed interface Step {
        /** Bir şey değişmez. */
        data object None : Step
        /** Oyun kesin biliniyor (PS5 bildirdi ya da kullanıcı seçti): profil hemen uygulanır. */
        data class Apply(val key: String) : Step
        /** Oyun yalnız paketten tanındı: alanlar sessizce değişmez, kullanıcıya sorulur. */
        data class Offer(val key: String) : Step
        /** Başka bir oyuna geçildi ve onun profili yok: genel alanlara dönülür. */
        data object UseGeneral : Step
    }

    /**
     * Oyun değişince ne yapılır. [active]: uygulanan profilin anahtarı (genel alanlarda null). [declined]: bu oturumda
     * geri alınan ya da istenmeyen profiller. Oyun bilinmez olursa son uygulanan profil kalır.
     */
    fun decide(active: String?, game: String?, source: GameSense.Source?, areaSource: AreaSource,
               exists: (String) -> Boolean, declined: (String) -> Boolean): Step {
        if (source == null) return Step.None
        val key = key(game, areaSource) ?: return Step.None
        if (key == active) return Step.None
        if (exists(key) && !declined(key)) return if (source == GameSense.Source.PACK) Step.Offer(key) else Step.Apply(key)
        return if (active == null) Step.None else Step.UseGeneral
    }

    /**
     * Profil eklendi ya da kullanıldı: aynı anahtarlı eskisi çıkar, yenisi sona eklenir (sıra son kullanımdır);
     * [MAX_COUNT]'u aşınca baştan, en uzun süredir kullanılmayan silinir.
     */
    fun <T> saved(profiles: List<T>, profile: T, key: (T) -> String): List<T> {
        val id = key(profile)
        return (profiles.filter { key(it) != id } + profile).takeLast(MAX_COUNT)
    }
}

/** Kalıcı alan durumu: uygulamada AppSettings, testlerde bellek. */
interface AreaStore {
    /** Şu an okunan alanlar. */
    var areas: AreaSet
    var areaProfiles: List<AreaProfile>
    /** Şu an uygulanan profilin anahtarı; genel alanlar kullanılıyorsa null. */
    var activeAreaProfile: String?
    /** Profil uygulanırken kenara alınan genel alanlar; profil bırakılınca geri gelir. */
    var generalAreas: AreaSet?
}

fun AreaStore.findAreaProfile(key: String?): AreaProfile? = key?.let { id -> areaProfiles.firstOrNull { it.key == id } }

/** Profil uygulanır: genel alanlar (henüz kenarda değilse) saklanır, okunan alanlar profilinki olur. */
fun AreaStore.useAreaProfile(profile: AreaProfile) {
    if (generalAreas == null) generalAreas = areas
    activeAreaProfile = profile.key
    areaProfiles = AreaProfiles.saved(areaProfiles, profile) { it.key }
    areas = profile.areas
}

/** Profil bırakılır, genel alanlar geri gelir (profilde kaydedilmemiş değişiklikler bırakılır). */
fun AreaStore.useGeneralAreas() {
    val general = generalAreas
    generalAreas = null
    activeAreaProfile = null
    if (general != null) areas = general
}

/** Okunan alanlar [game] için kaydedilir ve o profil etkin olur. */
fun AreaStore.saveAreaProfile(game: String, source: AreaSource) {
    val key = AreaProfiles.key(game, source) ?: return
    val current = areas
    areaProfiles = AreaProfiles.saved(areaProfiles, AreaProfile(key, game, current)) { it.key }
    activeAreaProfile = key
    if (generalAreas == null) generalAreas = current
}

/** Profil silinir; etkinse genel alanlara dönülür. */
fun AreaStore.deleteAreaProfile(key: String) {
    if (activeAreaProfile == key) useGeneralAreas()
    areaProfiles = areaProfiles.filter { it.key != key }
}

/** Etkin profil var ve okunan alanlar ondan farklı: "Profile kaydet" gösterilir. */
val AreaStore.areasDifferFromProfile: Boolean get() = findAreaProfile(activeAreaProfile)?.let { it.areas != areas } ?: false

/** Uygulama açılırken: oturum sürerken kapandıysa açık kalan profil kapanır. */
fun AreaStore.restoreAreas() {
    if (activeAreaProfile != null || generalAreas != null) useGeneralAreas()
}

/**
 * Oturumdaki oyuna göre alanlar (Mac ve iPhone'daki GameAreas, Windows'taki AppController ile aynı kurallar): oyun
 * değişince profil uygulanır, önerilir ya da genel alanlara dönülür; "Geri al", "Kullan/Hayır", kaydetme ve silme.
 * [changed]: ekranlar yenilensin (profil uygulandı ya da öneri değişti).
 */
class GameAreas(private val source: AreaSource = AreaSource.STREAM, private val changed: () -> Unit = {}) {
    @Volatile private var store: AreaStore? = null
    private val declined = mutableSetOf<String>()
    /** Paketten tanınan oyunun önerilen profili. */
    @Volatile var offered: String? = null
        private set
    /** Oynanan oyun (kaydetme hedefi). */
    @Volatile var game: String? = null
        private set

    val running: Boolean get() = store != null
    /** Uygulanan profilin oyunu; genel alanlarda null. */
    val activeGame: String? get() = store?.let { it.findAreaProfile(it.activeAreaProfile)?.game }
    /** Önerilen profilin oyunu. */
    val offeredGame: String? get() = store?.findAreaProfile(offered)?.game
    /** Alanların kaydedilebileceği oyun: etkin profilin ya da oynanan oyun (çeviri sürerken). */
    val target: String? get() = if (running) activeGame ?: game else null

    /** Çeviri başladı. */
    @Synchronized fun start(store: AreaStore) {
        this.store = store
        declined.clear()
        offered = null
        changed()
    }

    /** Oyun ya da nereden bilindiği değişti. */
    @Synchronized fun follow(current: RunningGame?, from: GameSense.Source?) {
        game = current?.name
        val store = store
        if (store == null) {
            changed()
            return
        }
        val step = AreaProfiles.decide(store.activeAreaProfile, current?.name, from, source,
            exists = { store.findAreaProfile(it) != null }, declined = { it in declined })
        offered = (step as? AreaProfiles.Step.Offer)?.key
        when (step) {
            is AreaProfiles.Step.Apply -> store.findAreaProfile(step.key)?.let { store.useAreaProfile(it) }
            AreaProfiles.Step.UseGeneral -> store.useGeneralAreas()
            else -> Unit
        }
        changed()
    }

    /** "Kullan". */
    @Synchronized fun useOffered() {
        val store = store ?: return
        store.findAreaProfile(offered)?.let { store.useAreaProfile(it) }
        offered = null
        changed()
    }

    /** "Hayır": bu oturumda bir daha sorulmaz. */
    @Synchronized fun declineOffered() {
        offered?.let { declined += it }
        offered = null
        changed()
    }

    /** "Geri al": genel alanlara dönülür; bu oyunun profili bu oturumda yeniden uygulanmaz. */
    @Synchronized fun undo() {
        val store = store ?: return
        val key = store.activeAreaProfile ?: return
        declined += key
        store.useGeneralAreas()
        changed()
    }

    /** Okunan alanlar etkin profile, profil yoksa oynanan oyuna kaydedilir. */
    @Synchronized fun save() {
        val store = store ?: return
        val target = target ?: return
        AreaProfiles.key(target, source)?.let { declined -= it }
        store.saveAreaProfile(target, source)
        changed()
    }

    /** Etkin profil silinir. */
    @Synchronized fun delete() {
        val store = store ?: return
        store.deleteAreaProfile(store.activeAreaProfile ?: return)
        changed()
    }

    /** Çeviri durdu: profil bırakılır (kaydedilmemiş alan değişiklikleri de). */
    @Synchronized fun stop() {
        store?.restoreAreas()
        store = null
        offered = null
        declined.clear()
        changed()
    }
}
