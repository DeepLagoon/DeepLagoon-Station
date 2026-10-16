import unittest

from check_physics_shapes import check_text


class PhysicsShapeChecks(unittest.TestCase):
    def test_inverted_strobe_bounds(self):
        errors = check_text('shape: !type:PhysShapeAabb\n  bounds: "-0.2,0.5,0.2,0.35"\n')
        self.assertEqual(1, len(errors))
        self.assertIn("left < right and bottom < top", errors[0][1])

    def test_normalized_and_default_boxes(self):
        self.assertEqual([], check_text('a: !type:PhysShapeAabb\n  bounds: "-0.2,0.35,0.2,0.5"\nb: !type:PhysShapeAabb {}'))

    def test_collapsed_and_nonfinite_geometry(self):
        for value in ('"0,0,0,1"', '"0,0,nan,1"', '"0,0,1,inf"'):
            self.assertTrue(check_text(f'shape: !type:PhysShapeAabb\n  bounds: {value}'))
        self.assertTrue(check_text('shape: !type:PolygonShape {}'))
        self.assertTrue(check_text('shape: !type:PolygonShape\n  vertices: ["0,0", "1,1", "2,2"]'))

    def test_vertex_order_and_comments(self):
        self.assertEqual([], check_text('shape: !type:PolygonShape\n  vertices: ["0,0", "0,1", "1,0"]'))
        self.assertEqual([], check_text('# shape: !type:PhysShapeAabb\n# bounds: "1,1,-1,-1"'))

    def test_reports_invalid_yaml(self):
        self.assertTrue(check_text('shape: !type:PhysShapeAabb\n  bounds: [unfinished'))


if __name__ == "__main__":
    unittest.main()
