from pathlib import Path
import tempfile
import unittest

from controls_reference import ControlsReference, preserved_movement, CONTROLS_PATH


class ControlsTests(unittest.TestCase):
    def setUp(self):
        self.reference = ControlsReference(Path(__file__).resolve().parents[2])

    def test_modifiers_mouse_keys_and_alternative_bindings(self):
        self.assertEqual(self.reference.key('TryPullObject'), '<kbd>Ctrl + ЛКМ</kbd>')
        self.assertEqual(self.reference.key('ThrowItemInHand'), '<kbd>Ctrl + Q</kbd>')
        self.assertEqual(self.reference.key('AltActivateItemInWorld'),
                         '<kbd>Alt + ЛКМ</kbd> или <kbd>Alt + E</kbd>')
        self.assertEqual(self.reference.key('CameraReset'), '<kbd>Num 8</kbd>')

    def test_bindings_are_loaded_from_the_supplied_game_tree(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'Resources').mkdir()
            (root / 'Resources/keybinds.yml').write_text(
                'binds:\n- function: Use\n  key: Z\n  mod1: Shift\n', encoding='utf-8')
            self.assertEqual(ControlsReference(root).key('Use'), '<kbd>Shift + Z</kbd>')

    def test_unknown_dynamic_action_fails_instead_of_inventing_a_key(self):
        for text in ['[keybind="NoSuchBinding"]', '[action: NoSuchBinding]',
                     'действие <code>NoSuchBinding</code>']:
            with self.subTest(text=text), self.assertRaises(ValueError):
                self.reference.replace_markers(text)

    def test_exported_html_and_markdown_actions_are_resolved_but_prototypes_stay(self):
        self.assertEqual(self.reference.replace_markers('действием <code>Use</code> и `SwapHands`'),
                         'клавишей <kbd>ЛКМ</kbd> и <kbd>X</kbd>')
        self.assertEqual(self.reference.replace_markers('<code>Welder</code> и `Water`'),
                         '<code>Welder</code> и `Water`')

    def test_raw_game_tags_accept_both_forms(self):
        self.assertEqual(self.reference.replace_markers('[keybind="Use"][keybind="Drop"/]'),
                         '<kbd>ЛКМ</kbd><kbd>Q</kbd>')
        self.assertEqual(self.reference.replace_markers('[действие: `Use`]'), '<kbd>ЛКМ</kbd>')

    def test_human_movement_is_preserved_byte_for_byte_with_subheadings(self):
        movement = '<h2 id="движение">Движение</h2>\r\n<p>Авторский Alt + WASD.</p>\n<h3>Тяга</h3><p>Текст.</p>\n'
        previous = '<p>Старое вступление</p>' + movement + '<h2>Руки</h2><p>Старый текст.</p>'
        output = self.reference.editorial_body(CONTROLS_PATH, previous)
        self.assertEqual(preserved_movement(output), movement)
        self.assertNotIn('Старый текст', output)
        self.assertIn('Инвентарь и хранилища', output)
        self.assertIn('<kbd>Ctrl + Q</kbd>', output)

    def test_missing_preserved_section_stops_publication_plan(self):
        with self.assertRaises(ValueError):
            self.reference.controls_body('<h2>Movement</h2><p>Something else.</p>')

    def test_editorial_paragraph_repair_leaves_neighbours_and_links_intact(self):
        before = '<p><a href="/ru/keep">Соседний текст</a></p>'
        action = '<p>Меню строительства: действие <code>OpenCraftingMenu</code>.</p>'
        after = '<p>Следующий абзац.</p>'
        output = self.reference.editorial_body('guidebook/in_game/engineering/construction', before + action + after)
        self.assertTrue(output.startswith(before))
        self.assertTrue(output.endswith(after))
        self.assertIn('<kbd>G</kbd>', output)
        self.assertNotIn('действие', output)

    def test_same_key_does_not_collapse_different_networking_instructions(self):
        output = self.reference.editorial_body('guidebook/in_game/engineering/networking',
            '<p>сохранить адрес действие <code>Use</code></p>'
            '<p>список устройств действие <code>Use</code></p>')
        self.assertIn('сохранить адрес', output)
        self.assertIn('Откроется окно управления', output)
        self.assertEqual(output.count('<kbd>ЛКМ</kbd>'), 2)

    def test_audit_counts_known_actions_without_counting_prototype_ids(self):
        result = self.reference.audit([{'path': 'a', 'content':
            '[keybind="Use"] <code>SwapHands</code> <code>Welder</code>'},
            {'path': 'b', 'content': '[action: Missing]'}])
        self.assertEqual(result['pagesScanned'], 2)
        self.assertEqual(result['markers'], 3)
        self.assertEqual(result['unknownActions'], ['Missing'])


if __name__ == '__main__':
    unittest.main()
