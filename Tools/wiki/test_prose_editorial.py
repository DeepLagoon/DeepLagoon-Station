from pathlib import Path
from types import SimpleNamespace
import unittest

from prose_editorial import editorial_prose, HEADING_EDITS, SOURCE_FILES


class ProseEditorialTests(unittest.TestCase):
    def test_heading_translation_preserves_the_existing_anchor(self):
        source = '<h2 id="заземляющие-штаны" class="existing">Заземляющие штаны</h2><p>Описание.</p>'
        result = editorial_prose('guidebook/in_game/engineering/tesla-engine', source)
        self.assertEqual(result,
            '<h2 id="заземляющие-штаны" class="existing">Заземляющие стержни</h2><p>Описание.</p>')

    def test_language_repairs_do_not_leak_to_other_paths_or_attributes(self):
        source = '<h2 id="мощный-мост">Мощный мост</h2><a title="Мощный мост" href="/same">ссылка</a>'
        self.assertEqual(editorial_prose('guidebook/in_game/other', source), source)
        result = editorial_prose('guidebook/in_game/engineering/solar-panels', source)
        self.assertIn('>Подключение к электросети</h2>', result)
        self.assertIn('id="мощный-мост"', result)
        self.assertIn('title="Мощный мост" href="/same"', result)

    def test_known_reagent_heading_uses_game_localization(self):
        data = SimpleNamespace(raw={'reagent': {'Bicaridine': {}}},
                               name=lambda kind, identifier: 'бикаридин')
        result = editorial_prose('guidebook/in_game/medical/medicine',
            '<h3 id="old" data-reagent="Bicaridine">Bicaridine</h3>', data)
        self.assertEqual(result, '<h3 id="old" data-reagent="Bicaridine">Бикаридин</h3>')

    def test_missing_localization_and_valid_proper_names_are_preserved(self):
        data = SimpleNamespace(raw={'reagent': {'Sample': {}}},
                               name=lambda kind, identifier: 'reagent-name-sample')
        source = '<h2>Nanotrasen/NT</h2><h3>LOOC/OOC</h3><h3>Sample</h3>'
        self.assertEqual(editorial_prose('guidebook/in_game/medical/medicine', source, data), source)

    def test_protected_pages_remain_exact_even_with_bad_heading_and_known_reagent(self):
        source = '<h2>Гир</h2><h3>Bicaridine</h3>'
        data = SimpleNamespace(raw={'reagent': {'Bicaridine': {}}}, name=lambda *args: 'бикаридин')
        for path in ['rules', 'guidebook/server-rules/rule', 'guidebook/in_game/lore/sol',
                     'guidebook/in_game/books/space-law', 'guidebook/in_game/sop/security']:
            with self.subTest(path=path):
                self.assertEqual(editorial_prose(path, source, data), source)

    def test_book_receives_the_same_reviewed_botany_translation(self):
        source = '<h2 id="keep">перекрестное осуждение</h2><p>Остальной текст.</p>'
        result = editorial_prose('guidebook/in_game/books/leaf-lovers-secret', source)
        self.assertEqual(result, '<h2 id="keep">Перекрёстное опыление</h2><p>Остальной текст.</p>')

    def test_factually_wrong_atmosphere_translation_is_scoped_and_fixed(self):
        source = '<p>Они встречаются в атмосфере.</p><span title="Они встречаются в атмосфере.">Хранилище</span>'
        result = editorial_prose('guidebook/in_game/engineering/gas-mining-and-storage', source)
        self.assertIn('<p>Они находятся в атмосферном отсеке.</p>', result)
        self.assertIn('title="Они встречаются в атмосфере."', result)

    def test_split_surgery_heading_merges_without_losing_either_anchor(self):
        source = ('<h2 id="first">Почему не пересадили орган</h2>\n'
                  '<h2 id="second">Делать что-нибудь?</h2><p>Проверка органа.</p>')
        result = editorial_prose('guidebook/in_game/imported/shitmed/medical/organ-manipulation', source)
        self.assertEqual(result, '<h2 id="first">Почему пересаженный орган не работает?'
                         '<span id="second"></span></h2><p>Проверка органа.</p>')

    def test_glossary_corrects_terms_without_rewriting_definitions(self):
        source = '<h2 id="cap">Капсула</h2><p>Определение со ссылкой <a href="/ru/rules">правила</a>.</p>'
        result = editorial_prose('guidebook/in_game/glossary', source)
        self.assertEqual(result, '<h2 id="cap">Капитан (Cap)</h2><p>Определение со ссылкой <a href="/ru/rules">правила</a>.</p>')

    def test_every_reviewed_group_has_a_real_guidebook_source(self):
        station = Path(__file__).resolve().parents[2]
        self.assertFalse(HEADING_EDITS.keys() - SOURCE_FILES.keys())
        for path in SOURCE_FILES.values():
            with self.subTest(path=path):
                self.assertTrue((station / path).is_file())

    def test_raw_markdown_headings_inside_exported_code_are_repaired(self):
        source = '<pre><code>## Заземляющие штаны\n\nТекст.\n  ### Tesla Coils\n</code></pre>'
        result = editorial_prose('guidebook/in_game/engineering/tesla-engine', source)
        self.assertEqual(result, '<pre><code>## Заземляющие стержни\n\nТекст.\n  ### Катушки Теслы\n</code></pre>')

    def test_raw_reagent_heading_uses_localization_without_touching_other_code(self):
        data = SimpleNamespace(raw={'reagent': {'Bicaridine': {}}}, name=lambda *args: 'бикаридин')
        source = '<pre><code>## Bicaridine\nBicaridine = Prototype\n</code></pre>'
        result = editorial_prose('guidebook/in_game/medical/medicine', source, data)
        self.assertEqual(result, '<pre><code>## Бикаридин\nBicaridine = Prototype\n</code></pre>')


if __name__ == '__main__':
    unittest.main()
