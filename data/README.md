# Data

## `fmcg_sales.csv`

- **Source:** [FMCG Daily Sales Data (2022–2024)](https://www.kaggle.com/datasets/beatafaron/fmcg-daily-sales-data-to-2022-2024)
  on Kaggle, by Beata Faron.
- **License:** [CC0 1.0 Universal (Public Domain)](https://creativecommons.org/publicdomain/zero/1.0/).
  It can be copied, modified and redistributed without permission, which is why it is included in
  this repo.
- **Contents:** synthetic daily FMCG sales for 2022–2024: 190,757 rows and 14 columns (date, SKU,
  brand, segment, category, channel, region, pack type, unit price, promotion flag, delivery days,
  stock, delivered quantity, units sold). The data is generated, not real company sales.

## `brands.csv`

A small 14-row dimension table (brand → category, manufacturer, launch year) created for this
project so there is a second table to `JOIN` against. The manufacturer names and launch years are
fictional. It is released under the repo's [MIT license](../LICENSE).
