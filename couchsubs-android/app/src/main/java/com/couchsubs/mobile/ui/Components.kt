// SPDX-License-Identifier: AGPL-3.0-only
// Tuvaldeki (design-canvas, Telefon-*) tekrar eden parçalar: başlık satırı, durum karosu, paket karosu, segmentli
// seçim, kod haneleri, satır listesi. Simgeler kutusuzdur; renkli kare ve tonlu hap yoktur.
package com.couchsubs.mobile.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.couchsubs.mobile.R

/** Logo (36), isteğe bağlı başlık ve sağda simge düğmeleri; en az 44 yükseklik (büyük yazıda uzar). */
@Composable
fun TopBar(title: String? = null, onBack: (() -> Unit)? = null, onClose: (() -> Unit)? = null, actions: @Composable RowScope.() -> Unit = {}) {
    Row(Modifier.fillMaxWidth().heightIn(min = Tokens.Control.icon), verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(Tokens.Space.gap)) {
        when {
            onBack != null -> IconAction(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.back), onBack)
            else -> Image(painterResource(R.mipmap.icon), null, Modifier.size(36.dp))
        }
        if (title != null) Text(title, style = Tokens.Type.title, color = Tokens.Color.text, modifier = Modifier.weight(1f), maxLines = 1,
            overflow = TextOverflow.Ellipsis)
        else Spacer(Modifier.weight(1f))
        actions()
        if (onClose != null) IconAction(Icons.Filled.Close, stringResource(R.string.close), onClose)
    }
}

/** Simge düğmesi; ad [IconSquare]'dan okunur, simgenin kendi açıklaması yoktur. */
@Composable
fun IconAction(icon: ImageVector, label: String, onClick: () -> Unit) =
    IconSquare(label, onClick = onClick) { Icon(icon, null, tint = Tokens.Color.text, modifier = Modifier.size(20.dp)) }

/** Bir ayara ya da ekrana götüren satırın ve karonun sonundaki `›` (ikincil renk). */
@Composable
fun Chevron() = Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, null, tint = Tokens.Color.secondary, modifier = Modifier.size(20.dp))

@Composable
fun SettingsAction(onClick: () -> Unit) = IconAction(Icons.Filled.Settings, stringResource(R.string.settings), onClick)

/** Profil (cihazlar ve tercihler) simge düğmesi. */
@Composable
fun ProfileAction(onClick: () -> Unit) = IconAction(Icons.Filled.Person, stringResource(R.string.profile), onClick)

/** Ekran başlığı (display) ve altında açıklama; üstte isteğe bağlı "Kurulum · Adım 1/3" (ikincil renk). */
@Composable
fun Heading(title: String, body: String? = null, step: String? = null) {
    Column(verticalArrangement = Arrangement.spacedBy(Tokens.Space.hair)) {
        step?.let { Text(it, style = Tokens.Type.caption, color = Tokens.Color.secondary) }
        Text(title, style = Tokens.Type.display, color = Tokens.Color.text)
        body?.let { Text(it, style = Tokens.Type.body, color = Tokens.Color.secondary) }
    }
}

/** Kutusuz karo simgesi (18 dp, ikincil renk; oyun paketi simgesi mor). */
@Composable
fun TileIcon(icon: ImageVector, tint: Color = Tokens.Color.secondary) =
    Icon(icon, null, tint = tint, modifier = Modifier.size(Tokens.Control.tileIcon))

/** Soldurulmuş (kapalı) içerik: %60; %40'ta ikincil yazı 4,5:1'in altına düşüyordu. */
private const val DIM_ALPHA = 0.6f

/**
 * Durum karosu: üstte etiket satırı (kutusuz simge, caption ad, sağda isteğe bağlı durum; [chevron] ise ilgili ayarı açtığını
 * söyleyen `›`), altında title değer ve isteğe bağlı not. Seçenek karosu olarak kullanılınca (seçim) `›` yoktur.
 */
@Composable
fun StatusTile(
    icon: ImageVector?, label: String, value: String, modifier: Modifier = Modifier,
    style: TileStyle = TileStyle.NORMAL, dim: Boolean = false, corner: (@Composable () -> Unit)? = null,
    note: String? = null, chevron: Boolean = false, onClick: (() -> Unit)? = null,
) {
    Tile(modifier.alpha(if (dim) DIM_ALPHA else 1f), style = style, onClick = onClick) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(Tokens.Space.inline)) {
            if (icon != null) TileIcon(icon)
            Text(label, style = Tokens.Type.caption, color = Tokens.Color.secondary, modifier = Modifier.weight(1f), maxLines = 1,
                overflow = TextOverflow.Ellipsis)
            corner?.invoke()
            if (chevron) Chevron()
        }
        Text(value, style = Tokens.Type.title, color = Tokens.Color.text, maxLines = 2, overflow = TextOverflow.Ellipsis)
        note?.let { Text(it, style = Tokens.Type.caption, color = Tokens.Color.secondary) }
    }
}

/**
 * Oyun paketi karosu: nötr kısaltma monogramı ve sağda durum, altında ad (headline) ve satır sayısı. [dim] yalnız monogramı
 * ve yazıları soldurur; köşedeki "Kapalı" etiketi okunur kalır. [toggled] verilirse karo paketi açıp kapatan anahtardır.
 */
@Composable
fun PackTile(
    code: String, name: String, lines: String, modifier: Modifier = Modifier, style: TileStyle = TileStyle.NORMAL,
    dim: Boolean = false, corner: (@Composable () -> Unit)? = null, toggled: Boolean? = null, onClick: (() -> Unit)? = null,
) {
    val alpha = if (dim) DIM_ALPHA else 1f
    Tile(modifier, style = style, onClick = onClick, toggled = toggled) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.alpha(alpha)) { GameMonogram(code) }
            Spacer(Modifier.weight(1f))
            corner?.invoke()
        }
        Column(Modifier.alpha(alpha), verticalArrangement = Arrangement.spacedBy(Tokens.Space.hair)) {
            Text(name, style = Tokens.Type.headline, color = Tokens.Color.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(lines, style = Tokens.Type.caption, color = Tokens.Color.secondary)
        }
    }
}

/** Oyun kısaltması ("GTA"): 44 dp, 8 köşe, surface2 zemin, nötr yazı. */
@Composable
fun GameMonogram(code: String) {
    Box(Modifier.size(Tokens.Control.icon).background(Tokens.Color.surface2, RoundedCornerShape(Tokens.Radius.inner)), contentAlignment = Alignment.Center) {
        Text(code, style = Tokens.Type.caption.copy(fontWeight = FontWeight.SemiBold), color = Tokens.Color.text)
    }
}

/** Seçili karonun sağ üstündeki işaret: 20 dp sarı daire içinde onay. */
@Composable
fun SelectedCheck() {
    Box(Modifier.size(20.dp).background(Tokens.Color.accent, CircleShape), contentAlignment = Alignment.Center) {
        Icon(Icons.Filled.Check, stringResource(R.string.selected), tint = Tokens.Color.onAccent, modifier = Modifier.size(14.dp))
    }
}

/** İki sütunlu satır (bento); iki karo aynı yükseklikte. */
@Composable
fun TwoColumns(left: @Composable (Modifier) -> Unit, right: @Composable (Modifier) -> Unit) {
    Row(Modifier.height(IntrinsicSize.Min), horizontalArrangement = Arrangement.spacedBy(Tokens.Space.gap)) {
        left(Modifier.weight(1f).fillMaxHeight())
        right(Modifier.weight(1f).fillMaxHeight())
    }
}

/**
 * Segmentli seçim: surface2 ray; seçili parça yükseltilmiş zemin, 1 px sarı çerçeve (raised, surface2'ye karşı tek başına
 * seçilmiyor) ve yarı kalın yazı; parıltı yok. Üç ve daha çok seçenekte yazı caption boyundadır; 360 dp genişlikte sığmayan
 * ad "…" ile kısalır. Erişilebilirlikte sekme grubu olarak okunur.
 */
@Composable
fun Segmented(options: List<String>, selected: Int, onSelect: (Int) -> Unit) {
    val base = if (options.size >= 3) Tokens.Type.caption else Tokens.Type.body
    Row(Modifier.fillMaxWidth().background(Tokens.Color.surface2, RoundedCornerShape(Tokens.Radius.control)).padding(Tokens.Space.hair)
        .selectableGroup(), horizontalArrangement = Arrangement.spacedBy(Tokens.Space.hair)) {
        options.forEachIndexed { index, option ->
            val on = index == selected
            val shape = RoundedCornerShape(Tokens.Radius.inner)
            Box(
                Modifier.weight(1f).heightIn(min = Tokens.Control.segment).clip(shape)
                    .background(if (on) Tokens.Color.raised else Color.Transparent)
                    .border(1.dp, if (on) Tokens.Color.accent else Color.Transparent, shape)
                    .selectable(selected = on, role = Role.Tab) { onSelect(index) }
                    .padding(Tokens.Space.hair),
                contentAlignment = Alignment.Center,
            ) {
                Text(option, style = base.copy(fontWeight = if (on) FontWeight.SemiBold else FontWeight.Medium),
                    color = if (on) Tokens.Color.text else Tokens.Color.secondary, maxLines = 1, overflow = TextOverflow.Ellipsis,
                    textAlign = TextAlign.Center)
            }
        }
    }
}

/** Sekiz haneli eşleme kodu kutuları (radius.inner); yazılan rakamlar kutulara dağılır, sıradaki kutu sarı çerçeveli. */
@Composable
fun CodeCells(code: String, length: Int = 8) {
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.fillMaxWidth()) {
        repeat(length) { index ->
            val digit = code.getOrNull(index)?.toString() ?: ""
            Box(
                Modifier.weight(1f).heightIn(min = 48.dp).background(Tokens.Color.surface2, RoundedCornerShape(Tokens.Radius.inner))
                    .border(1.dp, if (index == code.length) Tokens.Color.accent else Tokens.Color.borderStrong, RoundedCornerShape(Tokens.Radius.inner)),
                contentAlignment = Alignment.Center,
            ) { Text(digit, style = Tokens.Type.title, color = Tokens.Color.text) }
        }
    }
}

/** Geniş karonun içindeki satırlar, aralarında 1 px ayraç (karo iç boşluğu 4/16). */
@Composable
fun RowList(rows: List<@Composable () -> Unit>) {
    val shape = RoundedCornerShape(Tokens.Radius.tile)
    Column(
        Modifier.fillMaxWidth().background(Tokens.Color.surface, shape).border(1.dp, Tokens.Color.border, shape)
            .padding(horizontal = Tokens.Space.tile, vertical = Tokens.Space.hair),
    ) {
        rows.forEachIndexed { index, row ->
            if (index > 0) HorizontalDivider(color = Tokens.Color.border)
            row()
        }
    }
}

/**
 * Bilgi karosu (TASARIM.md InfoCard): durum değil ayar bilgisi gösteren düz karo; satırlar ([InfoRow]) arasında 1 px ayraç,
 * simge yok.
 */
@Composable
fun InfoCard(rows: List<@Composable () -> Unit>, modifier: Modifier = Modifier) {
    val shape = RoundedCornerShape(Tokens.Radius.tile)
    Column(modifier.fillMaxWidth().background(Tokens.Color.surface, shape).border(1.dp, Tokens.Color.border, shape).padding(Tokens.Space.inline)) {
        rows.forEachIndexed { index, row ->
            if (index > 0) HorizontalDivider(Modifier.padding(horizontal = Tokens.Space.inline), color = Tokens.Color.borderStrong)
            row()
        }
    }
}

/**
 * Bilgi karosunun satırı: solda ad (body, ikincil), sağda değer (body yarı kalın, tablo rakamlı). Bir ayara götüren satır
 * dokunulur ve sonunda `›` durur; basış dalgası `inner` köşeyle kırpılır. Dokunma alanı en az 48.
 */
@Composable
fun InfoRow(label: String, value: String, onClick: (() -> Unit)? = null) {
    Row(
        Modifier.fillMaxWidth().heightIn(min = Tokens.Control.touch).clip(RoundedCornerShape(Tokens.Radius.inner))
            .let { if (onClick != null) it.clickable(role = Role.Button, onClick = onClick) else it }
            .padding(horizontal = Tokens.Space.inline, vertical = Tokens.Space.hair),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(Tokens.Space.gap),
    ) {
        Text(label, style = Tokens.Type.body, color = Tokens.Color.secondary, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(value, style = Tokens.Type.value, color = Tokens.Color.text, maxLines = 1, overflow = TextOverflow.Ellipsis,
            textAlign = TextAlign.End, modifier = Modifier.weight(1f))
        if (onClick != null) Chevron()
    }
}

/** Kaydırıcının üstündeki satır: solda ad (caption, ikincil), sağda değer; bütün kaydırıcılarda aynı biçim. */
@Composable
fun SliderHeader(title: String, value: String) {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(Tokens.Space.gap)) {
        Text(title, style = Tokens.Type.caption, color = Tokens.Color.secondary, modifier = Modifier.weight(1f))
        Text(value, style = Tokens.Type.value, color = Tokens.Color.text)
    }
}

/** "ya da e-posta ile" gibi ortası yazılı ayraç. */
@Composable
fun DividerText(text: String) {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(Tokens.Space.gap)) {
        HorizontalDivider(Modifier.weight(1f), color = Tokens.Color.borderStrong)
        Text(text, style = Tokens.Type.caption, color = Tokens.Color.secondary)
        HorizontalDivider(Modifier.weight(1f), color = Tokens.Color.borderStrong)
    }
}

/** Bilgi ya da hata karosu: düz karo, solda kutusuz simge, metin verilen renkte (varsayılan uyarı). */
@Composable
fun NoteTile(text: String, color: Color = Tokens.Color.warning) {
    Tile(Modifier.fillMaxWidth()) {
        Row(verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(Tokens.Space.inline)) {
            if (color == Tokens.Color.warning) TileIcon(Icons.Filled.Warning, color)
            Text(text, style = Tokens.Type.body, color = color)
        }
    }
}

/**
 * Gelip giden not karosu (hata, uyarı): açılırken ve kapanırken solarak yer açar ya da kapatır. Kapanırken son metin
 * görünmeye devam eder; boş karo kaymaz.
 */
@Composable
fun AnimatedNote(text: String?, color: Color = Tokens.Color.warning) {
    val last = remember { arrayOfNulls<String>(1) }
    if (text != null) last[0] = text
    AnimatedVisibility(text != null, enter = fadeIn() + expandVertically(), exit = fadeOut() + shrinkVertically()) {
        (text ?: last[0])?.let { NoteTile(it, color) }
    }
}
