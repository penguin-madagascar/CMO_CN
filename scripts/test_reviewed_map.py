"""Keep reviewed geography bound to the same R88 members and zoom levels."""
import unittest

from reviewed_map import checked_data


class ReviewedMapTests(unittest.TestCase):
    def setUp(self):
        self.layers = {"Regions": [("巴厘省", -8.3412, 115.179)]}
        self.members = [["Regions", 0, "巴厘省", -8.3412, 115.179]]
        self.data = {
            "entities": [["Regions", 0], ["local", "city", "巴厘", 12, 23]],
            "labels": [[0, 5, 0, 0, None, 13, 0], [1, 5, 1, -2, None, 12, 0]],
            "symbols": [[5, 12, 23, 0, 0, "circle", 9, 1, 1, None]],
            "joins": [], "source_sha256": {},
        }

    def test_bound_region_and_independent_town(self):
        self.assertIs(self.data, checked_data(self.data, self.members, self.layers))

    def test_changed_upstream_member_is_not_assigned_to_another_city(self):
        self.layers["Regions"][0] = ("另一地区", -8.3412, 115.179)
        with self.assertRaisesRegex(ValueError, "changed"):
            checked_data(self.data, self.members, self.layers)

    def test_one_owner_per_member_and_zoom(self):
        self.data["joins"] = [[1, 5, "Regions", 0]]
        with self.assertRaisesRegex(ValueError, "Two labels"):
            checked_data(self.data, self.members, self.layers)

    def test_marker_does_not_bind_a_different_zoom(self):
        self.data["symbols"][0][0] = 6
        with self.assertRaisesRegex(ValueError, "owner"):
            checked_data(self.data, self.members, self.layers)

    def test_boolean_cannot_be_rendered_as_an_offset(self):
        self.data["labels"][0][2] = True
        with self.assertRaisesRegex(ValueError, "placement"):
            checked_data(self.data, self.members, self.layers)


if __name__ == "__main__":
    unittest.main()
