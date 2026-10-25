import unittest
from editorial_content import article_body, article_title, protected_page


class ContentTests(unittest.TestCase):
    def test_extracts_exact_manual_markup_even_with_unclosed_paragraphs(self):
        body = '<p>Вручную\n<h2 id="move">Движение</h2>\n<p>WASD<p>Alt</p>'
        source = '<div class="dlwiki"><main><article class="prose">' + body + '</article><aside>links</aside></main></div>'
        self.assertEqual(article_body(source), body)
        self.assertEqual(article_body(article_body(source)), body)

    def test_book_prose_extracts_nested_divs(self):
        self.assertEqual(article_body('<div class="prose">a<div>b</div>c</div>'), 'a<div>b</div>c')

    def test_policy_and_lore_are_protected(self):
        for path in ['rules', 'guidebook/in_game/server-rules/core', 'guidebook/in_game/imported/mono/lore/timeline', 'guidebook/in_game/imported/mono/factions/tsfmc-sop', 'guidebook/in_game/books/space-law']:
            self.assertTrue(protected_page(path), path)
            self.assertEqual(article_title(path, 'original'), 'original')
        self.assertFalse(protected_page('guidebook/in_game/mobs/moth'))


if __name__ == '__main__':
    unittest.main()
