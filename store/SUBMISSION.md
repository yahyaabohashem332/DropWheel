# دليل نشر Petal وبيعه على Microsoft Store

كل ما يحتاجه المتجر جاهز في المستودع. هذا الدليل يرتب الخطوات من إنشاء الحساب حتى النشر، ويذكر القيم التي
ستنسخها، والقرارات التي تبقى لك.

---

## 1. ما الذي جُهّز

| الشيء | مكانه |
|---|---|
| سكربت بناء حزمة المتجر (`.msix`) | `tools\scripts\build-store.ps1` |
| قالب البيان (AppxManifest) بكل أنواع الملفات التي يقرؤها Petal | `tools\store\AppxManifest.xml` (يُولَّد بـ `make_manifest.py`) |
| أيقونات الحزمة وبلاطاتها (77 صورة بكل المقاسات) | `tools\store\Assets` (تُولَّد بـ `make_assets.py`) |
| نصوص الحزمة بالعربية والإنجليزية | `tools\store\Strings` |
| صفحة المتجر بالإنجليزية والعربية | `store\listing\en-US.md` و`store\listing\ar-SA.md` |
| لقطات الشاشة 1920×1080 (ست لكل لغة) | `store\screenshots` (تُولَّد بـ `tools\store\make_screenshots.ps1`) |
| صور المتجر (Box art وPoster وأيقونة 300×300) | `store\logos` |
| سياسة الخصوصية (صفحة واحدة بالعربية والإنجليزية) | `store\privacy-policy.html` |
| شروط الترخيص الإضافية | `store\license-terms.md` |
| إشعارات الطرف الثالث والتراخيص | `store\THIRD-PARTY-NOTICES.md` (وتُنسخ مع البرنامج في مجلد `licenses`) |

وفي البرنامج نفسه، حين يعمل من حزمة المتجر:

- يبدأ مع Windows عبر «مهمة بدء» تابعة للحزمة (يراها المستخدم ويوقفها من إدارة المهام)، لا عبر السجل.
- لا يبحث عن تحديثات بنفسه؛ المتجر يحدّثه. وتقول بطاقة التحديثات في الإعدادات ذلك.
- يأتي «التحويل باستخدام Petal» في قائمة النقر بالزر الأيمن من الحزمة لكل أنواع الملفات التي يعرفها
  (104 امتدادات)، ولا يكتب في السجل.
- يقرأ ترخيص المتجر: أثناء الفترة التجريبية يظهر في رأس الإعدادات «الفترة التجريبية: بقي N يوم» وزر
  «شراء Petal»؛ وحين تنتهي تظهر نافذة «انتهت الفترة التجريبية» بزر شراء يفتح نافذة الشراء الخاصة بالمتجر،
  ويتوقف Petal حتى الشراء.
- يحمل معه البرامج الثلاثة التي كانت تُنزَّل عند الحاجة (Piper وllama.cpp وDeepFilterNet)، فلا ينزّل نسخة
  المتجر أي برنامج تنفيذي؛ تُنزَّل بيانات النماذج فقط.
- يعمل الأمر `petal` في الطرفية (اسم مستعار للتطبيق).
- يحفظ إعداداته في مجلد AppData المعتاد نفسه، فيفتحها Notepad ومستكشف الملفات، وتنتقل إعدادات من كان
  يستخدم نسخة برنامج التثبيت كما هي.

---

## 2. قبل البدء: قرارات لك

1. **الاسم.** «Petal» اسم شائع، وتستخدم Huawei علامات مثل «Petal Search» و«Petal Maps». ابحث في
   [WIPO Global Brand Database](https://branddb.wipo.int) وفي مكتب العلامات في بلدك قبل البيع، وإن كان
   الاسم محجوزًا في المتجر أو خشيت التعارض فاحجز اسمًا أوضح مثل «Petal File Wheel» أو «Petal: File Converter
   Wheel». السكربت يقبل أي اسم عبر `-DisplayName`.
2. **السعر.** اقتراح: سعر لمرة واحدة بين 7.99 و14.99 دولارًا (أداة إنتاجية تعمل دون اشتراك). المتجر يحوّله
   لكل سوق تلقائيًا.
3. **الفترة التجريبية.** البرنامج جاهز لأي مدة يختارها المتجر. الأنسب «15 يومًا» أو «30 يومًا»؛ أو «لا فترة
   تجريبية» إن أردت.
4. **بريد الدعم وموقع الويب.** يلزم بريد للدعم، ورابط عام لسياسة الخصوصية. استبدل `YOUR-SUPPORT-EMAIL` في
   `store\privacy-policy.html` وفي ملفي الصفحة، ثم انشر الصفحة على أي استضافة ثابتة مجانية (GitHub Pages أو
   Cloudflare Pages أو Netlify).
5. **نسخة برنامج التثبيت.** يمكن أن تبقى بجانب نسخة المتجر (تحديثاتها موقّعة)، أو تتوقف عنها. لا تثبّت
   النسختين على جهاز واحد معًا: تتشاركان الإعدادات، ولا يعمل إلا Petal واحد في المرة.

---

## 3. حساب المطوّر

1. افتح [Partner Center](https://partner.microsoft.com/dashboard) وسجّل في برنامج Windows and Xbox بحساب
   Microsoft.
2. اختر نوع الحساب: **فردي** (Individual) يكفي للبيع باسمك، أو **شركة** إن كان لديك كيان مسجّل (يحتاج تحققًا
   من الشركة، ويتيح اسم ناشر باسمها). تظهر الرسوم إن وُجدت عند التسجيل.
3. أكمل التحقق من الهوية.
4. لبيع تطبيق مدفوع: **Payout and tax profiles** — أضف حسابًا بنكيًا أو PayPal، واملأ نموذج الضرائب (لغير
   المقيمين في الولايات المتحدة: W-8BEN للأفراد). بدونه لا يمكن تسعير التطبيق.

---

## 4. حجز الاسم وأخذ قيم الهوية

1. **Apps and games › New product › MSIX or PWA app**، واكتب الاسم واحجزه.
2. افتح المنتج › **Product management › Product identity**، وانسخ القيم الثلاث:
   - `Package/Identity/Name` (مثل `12345PetalDev.Petal`)
   - `Package/Identity/Publisher` (مثل `CN=ABCD1234-5678-...`)
   - `Package/Properties/PublisherDisplayName` (مثل `Petal Dev`)

---

## 5. بناء الحزمة

من مجلد المستودع، في PowerShell 7:

```powershell
# 1) نسخة الإصدار (إن لم تكن مبنية بعد)
pwsh tools\scripts\build.ps1 -Version 1.5.0 -Notes "..."

# 2) حزمة المتجر بقيم الهوية من الخطوة 4
pwsh tools\scripts\build-store.ps1 -Version 1.5.0 `
    -IdentityName "12345PetalDev.Petal" `
    -Publisher "CN=ABCD1234-5678-..." `
    -PublisherDisplayName "Petal Dev" `
    -DisplayName "Petal"
```

الناتج: `dist\store\Petal-1.5.0.msix` (نحو 300 ميغابايت). لا يحتاج توقيعًا: المتجر يوقّع ما ينشره.

- السكربت يجلب أدوات Windows SDK (makeappx وmakepri) من NuGet إن لم تكن مثبتة.
- ويتحقق من SHA-256 لكل برنامج يضمّه، بالقيم نفسها التي يتحقق بها Petal.
- كل إصدار جديد: غيّر `-Version` فقط (المتجر يرفض رقمًا أصغر من السابق أو مساويًا له).
- عند إضافة صيغة جديدة إلى Petal: شغّل `python tools\store\make_manifest.py` (يذكّرك به اختبار إن نسيت).

**تجربة الحزمة قبل الرفع (اختيارية):** إمّا تفعيل «وضع المطوّر» في Windows على جهاز تجربة ثم
`build-store.ps1 ... -KeepLayout` و`Add-AppxPackage -Register dist\store\layout\AppxManifest.xml`، وإمّا
توقيعها بشهادة اختبار يثق بها جهاز التجربة (`-CertificatePfx`، ويجب أن يطابق موضوع الشهادة قيمة
`-Publisher`). يمكن أيضًا فحصها بـ Windows App Certification Kit من Windows SDK. جرّبها على جهاز لا يحمل
نسخة برنامج التثبيت.

---

## 6. التقديم (Submission) خطوة بخطوة

افتح المنتج › **Start your submission**.

### Pricing and availability
- **Markets:** كل الأسواق (أو اختر).
- **Pricing:** السعر الذي قررته في الخطوة 2.
- **Free trial:** المدة التي اخترتها.
- **Visibility:** Public.

### Properties
- **Category:** Productivity (أو Utilities & tools).
- **Privacy policy URL:** رابط الصفحة التي نشرتها.
- **Website / Support contact:** موقعك وبريد الدعم.
- **System requirements:** Windows 10 الإصدار 2004 فما بعد، x64. الموصى به: ذاكرة 8 غيغابايت، وبطاقة رسوميات
  تدعم DirectX 12 لتسريع أدوات الذكاء الاصطناعي.
- **Product declarations:** اتركها كما هي (Petal لا يعتمد على تعريفات أو خدمات NT خارجية)، ولا تفعّل خيار
  اختبار إمكانية الوصول ما لم تختبره بنفسك.

### Age ratings
املأ استبيان IARC: تطبيق أدوات (Utility/Productivity)؛ لا عنف ولا محتوى للبالغين؛ لا تواصل بين المستخدمين
(الإرسال إلى الهاتف يكون بين أجهزتك أنت على شبكتك)؛ لا مشاركة للموقع؛ لا مشتريات داخل التطبيق. النتيجة
المتوقعة: مناسب للجميع (3+).

### Packages
ارفع `dist\store\Petal-1.5.0.msix`. العائلة: Desktop.

### Store listings
أضف لغتين: **English (United States)** و**Arabic (Saudi Arabia)**، وانسخ كل جزء من ملفّي `store\listing`:
الوصف، والوصف القصير، والجديد، والمزايا، وكلمات البحث، والتعليقات. ثم:
- **Screenshots:** الصور الست من `store\screenshots\en-US` للإنجليزية، ومن `store\screenshots\ar-SA` للعربية،
  بالترتيب.
- **Store logos:** `store\logos\box-art-2160x2160.png` (1:1 Box art)، و`poster-art-1440x2160.png` (Poster)،
  و`tile-icon-300x300.png` (App tile icon).
- **Additional license terms:** من `store\license-terms.md`.

### Submission options › Restricted capabilities
سيطلب المتجر تبريرًا لصلاحيتين. انسخ النص الإنجليزي كما هو:

**runFullTrust:**
> Petal is a desktop (Win32) app packaged with MSIX. It shows its conversion wheel while files are dragged
> in File Explorer, which needs a low-level keyboard and mouse hook and full access to the files the user
> drops, so it must run as a full-trust desktop application.

**unvirtualizedResources:**
> Petal's quick toggles change the user's own Windows settings on request: dark mode
> (HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize), showing hidden files and file name
> extensions in File Explorer (HKCU\...\Explorer\Advanced). With registry virtualization these changes would
> not reach Windows. Petal also keeps its settings and log in %APPDATA%\Petal and %LOCALAPPDATA%\Petal
> without file system virtualization, so the "Open the Settings Folder" and "Open activity log" buttons work in
> File Explorer and Notepad, and settings carry over from the copy installed with Petal's own setup.
> Petal writes nothing outside the current user's profile.

### Notes for certification
> No account or sign-in is needed. To try Petal: start dragging any picture in File Explorer, tap Shift, and
> drop it on a petal; the result is saved next to the original. Ctrl and Alt switch to the tools and actions
> wheels. With nothing selected, Ctrl + Win + Z opens the wheels without files. Tray menu › Practice lets you
> try every wheel on a sample photo without changing anything. Some AI tools download their model the first
> time they are used (from Hugging Face or GitHub); their licences are shown before the download. Petal
> includes FFmpeg and espeak-ng (GPL-3.0) as separate programs; see THIRD-PARTY-NOTICES in the licenses
> folder.

ثم **Submit to the Store**. المراجعة تأخذ عادةً من يوم إلى ثلاثة أيام عمل. إن رُفضت الحزمة فالتقرير يذكر
السبب والبند، وأغلب الأسباب تُحل بتعديل النص أو التبرير.

---

## 7. التزامات التراخيص (مهم قبل البيع)

- **FFmpeg وespeak-ng تحت GPL-3.0.** Petal يشغّلهما برنامجين منفصلين، فلا يلزم فتح شيفرة Petal، لكن يلزمك
  إتاحة الشيفرة المصدرية لهما:
  1. بعد كل بناء، افتح `dist\Petal\licenses\FFmpeg-version.txt` لترى إصدار FFmpeg المضمَّن.
  2. نزّل شيفرة ذلك الإصدار من [ffmpeg.org](https://ffmpeg.org/download.html) (أو وسم الإصدار على GitHub)
     وسكربتات البناء من إصدار BtbN نفسه، وشيفرة espeak-ng من [rhasspy/espeak-ng](https://github.com/rhasspy/espeak-ng)،
     واحفظها عندك ثلاث سنوات.
  3. إن طلبها أحد عبر بريد الدعم، أرسلها له. هذا ما تعد به «Source code for the GPL programs» في الإشعارات.
- **شروط الترخيص الإضافية** في الصفحة توضح أن هذه المكوّنات تبقى تحت تراخيصها، فلا تتعارض شروط المتجر
  القياسية مع GPL.
- كل المكتبات والنماذج الأخرى بتراخيص متساهلة (MIT وApache-2.0 وBSD) تسمح بالبيع، ومذكورة في الإشعارات.

---

## 8. بعد النشر

- **تحديث:** ابنِ النسخة الجديدة بـ `build.ps1` ثم `build-store.ps1 -Version <الجديد>`، وفي Partner Center
  اختر **Update** على آخر تقديم، واستبدل الحزمة، وحدّث «What's new».
- **العروض والخصومات:** من Pricing and availability › Sale pricing.
- **التقييمات والتعليقات:** من Ratings and reviews، ويمكنك الرد عليها.
- **الأعطال:** Health › Failures يجمع تقارير الأعطال من أجهزة المستخدمين (دون بيانات شخصية).
- **روابط المتجر:** بعد النشر تجد رابط صفحة التطبيق في Product identity › URL، وشارة «Get it from
  Microsoft» من [Microsoft Store badge](https://apps.microsoft.com/badge).
