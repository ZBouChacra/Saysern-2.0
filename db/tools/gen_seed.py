#!/usr/bin/env python3
"""Generates db/migrations/V007__seed_geography.sql and V008__seed_translations.sql.

Source of the English names: the lists that were hard-coded in the original web app (171 countries, 524 cities,
119 phone codes, 74 nationalities). ISO codes and Arabic names are drafts written for this project; review them in the
back office (Lookups) - they are plain data, not code.
Usage:  python3 db/tools/gen_seed.py   (reads db/tools/original_lookups.json and db/tools/messages_en.json)
"""
import json, re, sys, pathlib

here = pathlib.Path(__file__).resolve().parent
src = json.load(open(here / "original_lookups.json", encoding="utf-8"))
msgs = json.load(open(here / "messages_en.json", encoding="utf-8"))
out = here.parent / "migrations"

COUNTRY = """Afghanistan|AF|أفغانستان
Albania|AL|ألبانيا
Algeria|DZ|الجزائر
Andorra|AD|أندورا
Angola|AO|أنغولا
Argentina|AR|الأرجنتين
Armenia|AM|أرمينيا
Australia|AU|أستراليا
Austria|AT|النمسا
Azerbaijan|AZ|أذربيجان
Bahrain|BH|البحرين
Bangladesh|BD|بنغلاديش
Belarus|BY|بيلاروس
Belgium|BE|بلجيكا
Benin|BJ|بنين
Bhutan|BT|بوتان
Bolivia|BO|بوليفيا
Bosnia and Herzegovina|BA|البوسنة والهرسك
Botswana|BW|بوتسوانا
Brazil|BR|البرازيل
Brunei|BN|بروناي
Bulgaria|BG|بلغاريا
Burkina Faso|BF|بوركينا فاسو
Burundi|BI|بوروندي
Cambodia|KH|كمبوديا
Cameroon|CM|الكاميرون
Canada|CA|كندا
Central African Republic|CF|جمهورية أفريقيا الوسطى
Chad|TD|تشاد
Chile|CL|تشيلي
China|CN|الصين
Colombia|CO|كولومبيا
Comoros|KM|جزر القمر
Congo|CG|الكونغو
Costa Rica|CR|كوستاريكا
Croatia|HR|كرواتيا
Cuba|CU|كوبا
Cyprus|CY|قبرص
Czech Republic|CZ|التشيك
Denmark|DK|الدنمارك
Djibouti|DJ|جيبوتي
Dominican Republic|DO|جمهورية الدومينيكان
DR Congo|CD|جمهورية الكونغو الديمقراطية
Ecuador|EC|الإكوادور
Egypt|EG|مصر
El Salvador|SV|السلفادور
Equatorial Guinea|GQ|غينيا الاستوائية
Eritrea|ER|إريتريا
Estonia|EE|إستونيا
Eswatini|SZ|إسواتيني
Ethiopia|ET|إثيوبيا
Fiji|FJ|فيجي
Finland|FI|فنلندا
France|FR|فرنسا
Gabon|GA|الغابون
Gambia|GM|غامبيا
Georgia|GE|جورجيا
Germany|DE|ألمانيا
Ghana|GH|غانا
Greece|GR|اليونان
Guatemala|GT|غواتيمالا
Guinea|GN|غينيا
Guinea-Bissau|GW|غينيا بيساو
Guyana|GY|غيانا
Haiti|HT|هايتي
Honduras|HN|هندوراس
Hungary|HU|المجر
Iceland|IS|آيسلندا
India|IN|الهند
Indonesia|ID|إندونيسيا
Iran|IR|إيران
Iraq|IQ|العراق
Ireland|IE|أيرلندا
Israel|IL|إسرائيل
Italy|IT|إيطاليا
Ivory Coast|CI|ساحل العاج
Jamaica|JM|جامايكا
Japan|JP|اليابان
Jordan|JO|الأردن
Kazakhstan|KZ|كازاخستان
Kenya|KE|كينيا
Kuwait|KW|الكويت
Kyrgyzstan|KG|قيرغيزستان
Laos|LA|لاوس
Latvia|LV|لاتفيا
Lebanon|LB|لبنان
Lesotho|LS|ليسوتو
Liberia|LR|ليبيريا
Libya|LY|ليبيا
Liechtenstein|LI|ليختنشتاين
Lithuania|LT|ليتوانيا
Luxembourg|LU|لوكسمبورغ
Madagascar|MG|مدغشقر
Malawi|MW|ملاوي
Malaysia|MY|ماليزيا
Maldives|MV|المالديف
Mali|ML|مالي
Malta|MT|مالطا
Mauritania|MR|موريتانيا
Mauritius|MU|موريشيوس
Mexico|MX|المكسيك
Moldova|MD|مولدوفا
Monaco|MC|موناكو
Mongolia|MN|منغوليا
Montenegro|ME|الجبل الأسود
Morocco|MA|المغرب
Mozambique|MZ|موزمبيق
Myanmar|MM|ميانمار
Namibia|NA|ناميبيا
Nepal|NP|نيبال
Netherlands|NL|هولندا
New Zealand|NZ|نيوزيلندا
Nicaragua|NI|نيكاراغوا
Niger|NE|النيجر
Nigeria|NG|نيجيريا
North Korea|KP|كوريا الشمالية
North Macedonia|MK|مقدونيا الشمالية
Norway|NO|النرويج
Oman|OM|عُمان
Pakistan|PK|باكستان
Palestine|PS|فلسطين
Panama|PA|بنما
Papua New Guinea|PG|بابوا غينيا الجديدة
Paraguay|PY|باراغواي
Peru|PE|بيرو
Philippines|PH|الفلبين
Poland|PL|بولندا
Portugal|PT|البرتغال
Qatar|QA|قطر
Romania|RO|رومانيا
Russia|RU|روسيا
Rwanda|RW|رواندا
Saudi Arabia|SA|المملكة العربية السعودية
Senegal|SN|السنغال
Serbia|RS|صربيا
Sierra Leone|SL|سيراليون
Singapore|SG|سنغافورة
Slovakia|SK|سلوفاكيا
Slovenia|SI|سلوفينيا
Somalia|SO|الصومال
South Africa|ZA|جنوب أفريقيا
South Korea|KR|كوريا الجنوبية
South Sudan|SS|جنوب السودان
Spain|ES|إسبانيا
Sri Lanka|LK|سريلانكا
Sudan|SD|السودان
Suriname|SR|سورينام
Sweden|SE|السويد
Switzerland|CH|سويسرا
Syria|SY|سوريا
Taiwan|TW|تايوان
Tajikistan|TJ|طاجيكستان
Tanzania|TZ|تنزانيا
Thailand|TH|تايلاند
Togo|TG|توغو
Trinidad and Tobago|TT|ترينيداد وتوباغو
Tunisia|TN|تونس
Turkey|TR|تركيا
Turkmenistan|TM|تركمانستان
Uganda|UG|أوغندا
Ukraine|UA|أوكرانيا
United Arab Emirates|AE|الإمارات العربية المتحدة
United Kingdom|GB|المملكة المتحدة
United States|US|الولايات المتحدة
Uruguay|UY|الأوروغواي
Uzbekistan|UZ|أوزبكستان
Venezuela|VE|فنزويلا
Vietnam|VN|فيتنام
Yemen|YE|اليمن
Zambia|ZM|زامبيا
Zimbabwe|ZW|زيمبابوي"""

NATIONALITY_AR = """Afghan|أفغاني
Albanian|ألباني
Algerian|جزائري
American|أمريكي
Argentinian|أرجنتيني
Australian|أسترالي
Austrian|نمساوي
Bahraini|بحريني
Bangladeshi|بنغلاديشي
Belgian|بلجيكي
Brazilian|برازيلي
British|بريطاني
Canadian|كندي
Chilean|تشيلي
Chinese|صيني
Colombian|كولومبي
Czech|تشيكي
Danish|دنماركي
Dutch|هولندي
Egyptian|مصري
Ethiopian|إثيوبي
Filipino|فلبيني
Finnish|فنلندي
French|فرنسي
German|ألماني
Ghanaian|غاني
Greek|يوناني
Hungarian|مجري
Indian|هندي
Indonesian|إندونيسي
Iranian|إيراني
Iraqi|عراقي
Irish|أيرلندي
Israeli|إسرائيلي
Italian|إيطالي
Japanese|ياباني
Jordanian|أردني
Kenyan|كيني
Kuwaiti|كويتي
Lebanese|لبناني
Libyan|ليبي
Malaysian|ماليزي
Mexican|مكسيكي
Moroccan|مغربي
New Zealander|نيوزيلندي
Nigerian|نيجيري
Norwegian|نرويجي
Omani|عماني
Pakistani|باكستاني
Palestinian|فلسطيني
Peruvian|بيروفي
Polish|بولندي
Portuguese|برتغالي
Qatari|قطري
Romanian|روماني
Russian|روسي
Saudi|سعودي
Singaporean|سنغافوري
South African|جنوب أفريقي
South Korean|كوري جنوبي
Spanish|إسباني
Sri Lankan|سريلانكي
Sudanese|سوداني
Swedish|سويدي
Swiss|سويسري
Syrian|سوري
Taiwanese|تايواني
Thai|تايلاندي
Tunisian|تونسي
Turkish|تركي
Emirati|إماراتي
Ukrainian|أوكراني
Vietnamese|فيتنامي
Yemeni|يمني"""

CITY_AR = """Afghanistan|كابول|قندهار|هرات|مزار شريف|جلال آباد
Albania|تيرانا|دوريس|فلورة|إلباسان|شكودرا
Algeria|الجزائر|وهران|قسنطينة|عنابة|البليدة
Argentina|بوينس آيرس|قرطبة|روساريو|مندوزا|لا بلاتا
Armenia|يريفان|غيومري|فانادزور
Australia|سيدني|ملبورن|بريسبان|بيرث|أديلايد|كانبرا
Austria|فيينا|غراتس|لينز|سالزبورغ|إنسبروك
Azerbaijan|باكو|غنجة|سومقايت
Bahrain|المنامة|المحرق|الرفاع|مدينة حمد|مدينة عيسى|سترة|البديع|جدحفص
Bangladesh|دكا|شيتاغونغ|خولنا|راجشاهي|سيلهيت
Belgium|بروكسل|أنتويرب|غنت|شارلروا|لييج|بروج
Brazil|ساو باولو|ريو دي جانيرو|برازيليا|سلفادور|فورتاليزا|بيلو هوريزونتي
Bulgaria|صوفيا|بلوفديف|فارنا|بورغاس
Canada|تورنتو|مونتريال|فانكوفر|كالغاري|إدمونتون|أوتاوا|وينيبيغ|مدينة كيبك|هاميلتون|كيتشنر
Chile|سانتياغو|فالبارايسو|كونسبسيون|أنتوفاغاستا
China|بكين|شنغهاي|قوانغتشو|شنزن|تشنغدو|هانغتشو|ووهان|نانجينغ
Colombia|بوغوتا|ميديلين|كالي|بارانكيا|قرطاجنة
Croatia|زغرب|سبليت|ريكا|أوسييك
Cuba|هافانا|سانتياغو دي كوبا|كاماغوي
Cyprus|نيقوسيا|ليماسول|لارنكا|بافوس
Czech Republic|براغ|برنو|أوسترافا|بلزن
Denmark|كوبنهاغن|آرهوس|أودنسه|آلبورغ
Egypt|القاهرة|الإسكندرية|الجيزة|شرم الشيخ|الأقصر|أسوان|الغردقة|بورسعيد|السويس|المنصورة|طنطا|الإسماعيلية
Estonia|تالين|تارتو
Ethiopia|أديس أبابا|دير داوا|ميكيلي|غوندر
Finland|هلسنكي|إسبو|تامبيري|توركو|أولو
France|باريس|مرسيليا|ليون|تولوز|نيس|نانت|ستراسبورغ|مونبلييه|بوردو|ليل
Georgia|تبليسي|باتومي|كوتايسي
Germany|برلين|ميونخ|هامبورغ|فرانكفورت|كولونيا|شتوتغارت|دوسلدورف|لايبزيغ|دورتموند|إيسن
Ghana|أكرا|كوماسي|تامالي|تاكورادي
Greece|أثينا|تيسالونيكي|باتراس|هيراكليون|لاريسا
Hungary|بودابست|ديبريتسين|سيجد|ميشكولتس|بيتش
Iceland|ريكيافيك|كوباغفوغور|هافنارفيوردور
India|مومباي|دلهي|بنغالور|حيدر آباد|تشيناي|كلكتا|بيون|أحمد آباد|جايبور|لكناو
Indonesia|جاكرتا|سورابايا|باندونغ|ميدان|سيمارانغ|بالي
Iran|طهران|أصفهان|مشهد|تبريز|شيراز
Iraq|بغداد|البصرة|أربيل|السليمانية|الموصل|كركوك|النجف|كربلاء|دهوك|الناصرية
Ireland|دبلن|كورك|ليميريك|غالواي|ووترفورد
Israel|تل أبيب|القدس|حيفا|بئر السبع|نتانيا
Italy|روما|ميلانو|نابولي|تورينو|فلورنسا|بولونيا|البندقية|جنوة
Japan|طوكيو|أوساكا|يوكوهاما|ناغويا|سابورو|كوبي|كيوتو|فوكوكا
Jordan|عمّان|الزرقاء|إربد|العقبة|مادبا|جرش|السلط|المفرق|الكرك|عجلون
Kazakhstan|ألماتي|نور سلطان|شيمكنت|قره غندى
Kenya|نيروبي|مومباسا|كيسومو|ناكورو|إلدوريت
Kuwait|مدينة الكويت|حولي|السالمية|الفروانية|الجهراء|الأحمدي|المنقف|الفحيحيل
Latvia|ريغا|دوغافبيلس|ليپاجا
Lebanon|بيروت|طرابلس|صيدا|صور|جونية|زحلة|بعلبك|جبيل|عاليه|البترون|النبطية|بشري|برمانا|إهدن|جبيل
Libya|طرابلس|بنغازي|مصراتة|سبها
Lithuania|فيلنيوس|كاوناس|كلايبيدا
Malaysia|كوالالمبور|جورج تاون|جوهور بهرو|إيبوه|شاه علم|كوتا كينابالو
Mexico|مكسيكو سيتي|غوادالاخارا|مونتيري|بويبلا|كانكون|تيخوانا
Morocco|الدار البيضاء|الرباط|مراكش|فاس|طنجة|أغادير
Netherlands|أمستردام|روتردام|لاهاي|أوترخت|أيندهوفن
New Zealand|أوكلاند|ويلينغتون|كرايستشيرش|هاميلتون|دنيدن
Nigeria|لاغوس|أبوجا|كانو|إبادان|بورت هاركورت
Norway|أوسلو|بيرغن|تروندهايم|ستافنغر
Oman|مسقط|صلالة|صحار|نزوى|صور|عبري|بركاء|الرستاق|السيب
Pakistan|كراتشي|لاهور|إسلام آباد|راولبندي|فيصل آباد|بيشاور
Palestine|رام الله|غزة|نابلس|الخليل|بيت لحم|جنين|طولكرم|أريحا|قلقيلية
Peru|ليما|أريكيبا|كوسكو|تروخيو
Philippines|مانيلا|كويزون سيتي|دافاو|سيبو|ماكاتي
Poland|وارسو|كراكوف|فروتسواف|غدانسك|بوزنان|لودز
Portugal|لشبونة|بورتو|براغا|كويمبرا|فارو
Qatar|الدوحة|الوكرة|الخور|لوسيل|دخان|مسيعيد|الريان
Romania|بوخارست|كلوج نابوكا|تيميشوارا|ياش|كونستانتا
Russia|موسكو|سانت بطرسبرغ|نوفوسيبيرسك|يكاترينبورغ|قازان
Saudi Arabia|الرياض|جدة|مكة المكرمة|المدينة المنورة|الدمام|الخبر|الظهران|تبوك|أبها|الطائف|حائل|نجران|الجبيل|ينبع
Senegal|داكار|تياس|سان لوي
Serbia|بلغراد|نوفي ساد|نيش
Singapore|سنغافورة
South Africa|جوهانسبرغ|كيب تاون|دوربان|بريتوريا|بورت إليزابيث
South Korea|سيول|بوسان|إنتشون|دايغو|دايجون|غوانغجو
Spain|مدريد|برشلونة|فالنسيا|إشبيلية|مالقة|بلباو
Sri Lanka|كولومبو|كاندي|غالي|جافنا
Sudan|الخرطوم|أم درمان|بورتسودان
Sweden|ستوكهولم|غوتنبرغ|مالمو|أوبسالا
Switzerland|زيورخ|جنيف|بازل|برن|لوزان
Syria|دمشق|حلب|حمص|اللاذقية|حماة|طرطوس|دير الزور|الرقة|إدلب|درعا
Taiwan|تايبيه|كاوهسيونغ|تايتشونغ|تاينان
Thailand|بانكوك|شيانغ ماي|فوكيت|باتايا|نونثابوري
Tunisia|تونس|صفاقس|سوسة|القيروان
Turkey|إسطنبول|أنقرة|إزمير|بورصة|أنطاليا|أضنة|غازي عنتاب|قونية|مرسين|قيصري
Uganda|كمبالا|غولو|ليرا|مبررارة
Ukraine|كييف|خاركيف|أوديسا|دنيبرو|لفيف
United Arab Emirates|أبوظبي|دبي|الشارقة|عجمان|رأس الخيمة|الفجيرة|أم القيوين|العين
United Kingdom|لندن|مانشستر|برمنغهام|ليدز|غلاسكو|ليفربول|إدنبرة|بريستول|شيفيلد|كارديف
United States|نيويورك|لوس أنجلوس|شيكاغو|هيوستن|فينيكس|فيلادلفيا|سان أنطونيو|سان دييغو|دالاس|سان خوسيه|ميامي|سان فرانسيسكو|سياتل|دنفر|بوسطن
Venezuela|كراكاس|ماراكايبو|فالنسيا|باركيسيميتو
Vietnam|مدينة هو تشي منه|هانوي|دا نانغ|هايفونغ|كان ثو
Yemen|صنعاء|عدن|تعز|الحديدة"""

ERR_AR = {
 "INVALID_CREDENTIALS": "البريد الإلكتروني أو كلمة المرور غير صحيحة.",
 "ACCOUNT_LOCKED": "هذا الحساب مقفل. حاول لاحقًا أو تواصل مع الدعم.",
 "DUPLICATE_EMAIL": "يوجد حساب بهذا البريد الإلكتروني. جرّب تسجيل الدخول.",
 "VALIDATION_FAILED": "يرجى تصحيح الحقول المحددة.",
 "CSRF_REJECTED": "تم حظر الطلب لحمايتك. أعد تحميل الصفحة وحاول مرة أخرى.",
 "LINK_EXPIRED": "انتهت صلاحية طلب ربط حساب Google. ابدأ تسجيل الدخول بواسطة Google من جديد.",
 "LINK_NOT_POSSIBLE": "هذا الحساب بلا كلمة مرور، لذا لا يمكن ربطه بهذه الطريقة.",
 "LAST_ADMIN": "يجب أن يبقى مسؤول نشط واحد على الأقل.",
 "CANNOT_MODIFY_SELF": "لا يمكنك تغيير دورك أو قفل حسابك بنفسك.",
 "USER_NOT_FOUND": "هذا المستخدم لم يعد موجودًا.",
 "INVALID_ROLE": "دور غير معروف.",
 "GOOGLE_NOT_CONFIGURED": "تسجيل الدخول بواسطة Google غير مُفعّل على هذا الخادم.",
 "SLOT_TAKEN": "هذا الوقت لم يعد متاحًا. يرجى اختيار وقت آخر.",
 "START_IN_PAST": "اختر وقتًا في المستقبل.",
 "CANNOT_BOOK_SELF": "لا يمكنك حجز موعد مع نفسك.",
 "PROFESSIONAL_NOT_FOUND": "هذا المحترف غير متاح للحجز.",
 "PROFESSION_NOT_FOUND": "هذه الخدمة لم تعد موجودة.",
 "INVALID_DURATION": "اختر مدة ضمن الحدود المسموحة.",
 "INVALID_TRANSITION": "لم يعد هذا الإجراء ممكنًا لهذا الموعد.",
 "FORBIDDEN_ACTION": "هذا الإجراء متاح للمحترف فقط.",
 "NOT_STARTED_YET": "يمكنك إكمال الموعد بعد أن يبدأ.",
 "NOT_COMPLETED": "يمكن تقييم المواعيد المكتملة فقط.",
 "REVIEW_EXISTS": "لقد قيّمت هذا الموعد من قبل.",
 "APPOINTMENT_NOT_FOUND": "هذا الموعد غير موجود.",
 "NOT_A_PROFESSIONAL": "غيّر نوع ملفك الشخصي إلى محترف أولًا.",
 "HIDE_PROFESSIONS_FIRST": "أخفِ خدماتك قبل التحويل إلى ملف عميل.",
 "DUPLICATE_PROFESSION": "أنت تقدّم هذه الخدمة بالفعل.",
 "INVALID_SERVICE": "هذه الخدمة لا تنتمي إلى الفئة المختارة.",
 "ROOM_NOT_FOUND": "هذه المحادثة غير موجودة.",
 "CANNOT_CHAT_SELF": "لا يمكنك مراسلة نفسك.",
 "ALERT_LIMIT": "وصلت إلى الحد الأقصى لعمليات البحث المحفوظة.",
 "TooManyRequests": "أنت تفعل ذلك بسرعة كبيرة. يرجى الانتظار قليلًا.",
 "SERVER_ERROR": "حدث خطأ من جهتنا. يرجى المحاولة مرة أخرى.",
 "google_state": "انتهت جلسة تسجيل الدخول بواسطة Google أو كانت غير صالحة. حاول مرة أخرى.",
 "google_denied": "تم إلغاء تسجيل الدخول بواسطة Google.",
 "google_token": "تعذّر التحقق من تسجيل الدخول بواسطة Google. حاول مرة أخرى.",
 "google_exchange": "تعذّر إكمال تسجيل الدخول بواسطة Google. حاول مرة أخرى.",
 "google_email_unverified": "بريدك الإلكتروني في Google غير مُوثّق، لذا لا يمكن استخدامه لتسجيل الدخول.",
 "google_conflict": "تعارض هذا التسجيل مع تسجيل آخر. حاول مرة أخرى.",
 "account_locked": "هذا الحساب مقفل. حاول لاحقًا أو تواصل مع الدعم.",
 "login_required": "يرجى تسجيل الدخول أولًا.",
}
EXTRA_ERR = {  # codes the API raises that the original UI had no text for
 "INVALID_SEX": ("The selected sex is not available.", "القيمة المختارة للجنس غير متاحة."),
 "INVALID_NATIONALITY": ("The selected nationality is not available.", "الجنسية المختارة غير متاحة."),
 "INVALID_COUNTRY": ("The selected country is not available.", "الدولة المختارة غير متاحة."),
 "INVALID_LANGUAGE": ("The selected language is not available.", "اللغة المختارة غير متاحة."),
 "INVALID_FREQUENCY": ("The selected frequency is not available.", "التكرار المختار غير متاح."),
 "UNAUTHENTICATED": ("Please sign in.", "يرجى تسجيل الدخول."),
}

def q(s): return "'" + s.replace("\\", "\\\\").replace("'", "''") + "'"
def slug(s):
    s = s.lower().replace("&", "and")
    return re.sub(r"[^a-z0-9]+", "-", s).strip("-")
def flag(iso): return "".join(chr(0x1F1E6 + ord(c) - 65) for c in iso)

countries = [l.split("|") for l in COUNTRY.splitlines()]
assert [c[0] for c in countries] == src["countries"], "country list differs from the original"
iso = {c[0]: c[1] for c in countries}; assert len(set(iso.values())) == len(iso), "duplicate ISO code"
phones = dict(src["phones"])
nat_ar = dict(l.split("|") for l in NATIONALITY_AR.splitlines())
assert list(nat_ar) == src["nationalities"], "nationality list differs from the original"
city_ar = {}
for l in CITY_AR.splitlines():
    p = l.split("|"); city_ar[p[0]] = p[1:]
assert set(city_ar) == set(src["cities"]), "city country sets differ"
for k, v in src["cities"].items(): assert len(v) == len(city_ar[k]), f"city count differs for {k}: {len(v)} vs {len(city_ar[k])}"

rows = []   # (type, code, parent_code, sort, metadata_json, en, ar)
for i, (name, code, ar) in enumerate(countries, 1):
    meta = {"iso2": code, "flag": flag(code)}
    if name in phones: meta["phoneCode"] = phones[name]
    rows.append(("country", code, None, i, json.dumps(meta, ensure_ascii=False), name, ar))
for name, cities in src["cities"].items():
    for i, (en, ar) in enumerate(zip(cities, city_ar[name]), 1):
        rows.append(("city", f"{iso[name]}-{slug(en)}", iso[name], i, None, en, ar))
for i, en in enumerate(src["nationalities"], 1):
    rows.append(("nationality", slug(en).replace("-", "_"), None, i, None, en, nat_ar[en]))
codes = [(r[0], r[1]) for r in rows]; assert len(codes) == len(set(codes)), "duplicate lookup code"

def chunks(seq, n):
    for i in range(0, len(seq), n): yield seq[i:i + n]

sql = ["-- V007: countries, cities, nationalities (generated by db/tools/gen_seed.py from the original app's lists).",
       "-- English names come from the original; ISO codes and Arabic names are drafts - review them in the back office (Lookups).",
       "",
       "CREATE TEMPORARY TABLE tmp_geo (type_code VARCHAR(50), code VARCHAR(100), parent_code VARCHAR(100), sort_order INT, metadata JSON, name_en VARCHAR(200), name_ar VARCHAR(200));"]
for part in chunks(rows, 100):
    sql.append("INSERT INTO tmp_geo VALUES")
    sql.append(",\n".join("(%s,%s,%s,%d,%s,%s,%s)" % (q(t), q(c), q(p) if p else "NULL", s, q(m) if m else "NULL", q(en), q(ar)) for t, c, p, s, m, en, ar in part) + ";")
sql += ["INSERT INTO lookup_values (type_code, code, sort_order, metadata) SELECT type_code, code, sort_order, metadata FROM tmp_geo WHERE parent_code IS NULL;",
        "INSERT INTO lookup_values (type_code, code, parent_id, sort_order, metadata)",
        "  SELECT g.type_code, g.code, p.id, g.sort_order, g.metadata FROM tmp_geo g JOIN lookup_values p ON p.type_code = 'country' AND p.code = g.parent_code WHERE g.parent_code IS NOT NULL;",
        "-- (a temporary table cannot be used twice in one statement, hence two inserts)",
        "INSERT INTO lookup_translations (value_id, language, name)",
        "  SELECT v.id, 'en', g.name_en FROM tmp_geo g JOIN lookup_values v ON v.type_code = g.type_code AND v.code = g.code;",
        "INSERT INTO lookup_translations (value_id, language, name)",
        "  SELECT v.id, 'ar', g.name_ar FROM tmp_geo g JOIN lookup_values v ON v.type_code = g.type_code AND v.code = g.code;",
        "DROP TEMPORARY TABLE tmp_geo;", ""]
(out / "V007__seed_geography.sql").write_text("\n".join(sql), encoding="utf-8")

# ---- translations: error messages parsed from the web client's former hard-coded table
missing = [k for k in msgs if k not in ERR_AR]; assert not missing, f"no Arabic text for {missing}"
trows = [("error." + k, v, ERR_AR[k]) for k, v in msgs.items()] + [("error." + k, en, ar) for k, (en, ar) in EXTRA_ERR.items()]
t = ["-- V008: UI texts (error messages for now). Generated by db/tools/gen_seed.py from db/tools/messages_en.json; edit them in the back office (Texts).", "",
     "INSERT INTO translations (language, translation_key, value, default_value) VALUES"]
t.append(",\n".join("('en',%s,%s,%s),\n('ar',%s,%s,%s)" % (q(k), q(en), q(en), q(k), q(ar), q(ar)) for k, en, ar in trows) + ";")
(out / "V008__seed_translations.sql").write_text("\n".join(t) + "\n", encoding="utf-8")
print(f"countries={sum(1 for r in rows if r[0]=='country')} cities={sum(1 for r in rows if r[0]=='city')} nationalities={sum(1 for r in rows if r[0]=='nationality')} messages={len(trows)}")
