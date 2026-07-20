# Настройки питания Lenovo ThinkBook 16 G8+ AGP

Дата фиксации: 14 июля 2026 года.

Процессор: AMD Ryzen AI 7 H 450, 8 ядер / 16 потоков.

## Идентификаторы режимов Windows

- «Лучшая энергоэффективность»: `961cc777-2547-4f9d-8174-7d86181b8a7a`
- «Сбалансированный»: `381b4222-f694-41f0-9685-ff5bb260df2e`
- На момент снимка «Лучшая энергоэффективность» активна и для сети, и для батареи.
- В Lenovo Vantage выбран режим «Бесшумная работа и экономия заряда аккумулятора».

## Текущая настройка «Лучшая энергоэффективность»

Одинакова для сети и батареи.

| Параметр Windows | Сеть | Батарея | Hex | Назначение |
|---|---:|---:|---:|---|
| `PERFEPP` | 100 | 100 | `0x64` | максимальный приоритет энергоэффективности, класс 0 |
| `PERFEPP1` | 100 | 100 | `0x64` | максимальный приоритет энергоэффективности, класс 1 |
| `PROCFREQMAX` | 2300 МГц | 2300 МГц | `0x8FC` | предел частоты, класс 0 |
| `PROCFREQMAX1` | 3000 МГц | 3000 МГц | `0xBB8` | предел частоты, класс 1 |
| `PROCTHROTTLEMAX` | 100% | 100% | `0x64` | максимальное состояние процессора; частота ограничивается отдельными параметрами выше |

### Команды для восстановления текущей настройки

Запускать в PowerShell или Terminal с правами администратора. После выполнения повторно выбрать «Лучшая энергоэффективность» в параметрах Windows либо переподключить питание.

```powershell
$mode = '961cc777-2547-4f9d-8174-7d86181b8a7a'

powercfg /setacvalueindex $mode SUB_PROCESSOR PERFEPP 100
powercfg /setdcvalueindex $mode SUB_PROCESSOR PERFEPP 100
powercfg /setacvalueindex $mode SUB_PROCESSOR PERFEPP1 100
powercfg /setdcvalueindex $mode SUB_PROCESSOR PERFEPP1 100

powercfg /setacvalueindex $mode SUB_PROCESSOR PROCFREQMAX 2300
powercfg /setdcvalueindex $mode SUB_PROCESSOR PROCFREQMAX 2300
powercfg /setacvalueindex $mode SUB_PROCESSOR PROCFREQMAX1 3000
powercfg /setdcvalueindex $mode SUB_PROCESSOR PROCFREQMAX1 3000
```

## Предыдущее мягкое состояние

Это состояние полностью повторяло заводские батарейные значения режима на питании от сети.

| Параметр | Сеть | Батарея |
|---|---:|---:|
| `PERFEPP` | 100 | 100 |
| `PERFEPP1` | 90 | 90 |
| `PROCFREQMAX` | 2500 МГц | 2500 МГц |
| `PROCFREQMAX1` | 3300 МГц | 3300 МГц |

### Команды возврата к мягкому состоянию

```powershell
$mode = '961cc777-2547-4f9d-8174-7d86181b8a7a'

powercfg /setacvalueindex $mode SUB_PROCESSOR PERFEPP 100
powercfg /setdcvalueindex $mode SUB_PROCESSOR PERFEPP 100
powercfg /setacvalueindex $mode SUB_PROCESSOR PERFEPP1 90
powercfg /setdcvalueindex $mode SUB_PROCESSOR PERFEPP1 90

powercfg /setacvalueindex $mode SUB_PROCESSOR PROCFREQMAX 2500
powercfg /setdcvalueindex $mode SUB_PROCESSOR PROCFREQMAX 2500
powercfg /setacvalueindex $mode SUB_PROCESSOR PROCFREQMAX1 3300
powercfg /setdcvalueindex $mode SUB_PROCESSOR PROCFREQMAX1 3300
```

## Исходное состояние «Лучшая энергоэффективность» при первой проверке

| Параметр | Сеть | Батарея |
|---|---:|---:|
| `PERFEPP` | 50 | 100 |
| `PERFEPP1` | 50 | 90 |
| `PROCFREQMAX` | 3500 МГц | 2500 МГц |
| `PROCFREQMAX1` | 3500 МГц | 3300 МГц |

### Команды возврата к исходному состоянию

```powershell
$mode = '961cc777-2547-4f9d-8174-7d86181b8a7a'

powercfg /setacvalueindex $mode SUB_PROCESSOR PERFEPP 50
powercfg /setdcvalueindex $mode SUB_PROCESSOR PERFEPP 100
powercfg /setacvalueindex $mode SUB_PROCESSOR PERFEPP1 50
powercfg /setdcvalueindex $mode SUB_PROCESSOR PERFEPP1 90

powercfg /setacvalueindex $mode SUB_PROCESSOR PROCFREQMAX 3500
powercfg /setdcvalueindex $mode SUB_PROCESSOR PROCFREQMAX 2500
powercfg /setacvalueindex $mode SUB_PROCESSOR PROCFREQMAX1 3500
powercfg /setdcvalueindex $mode SUB_PROCESSOR PROCFREQMAX1 3300
```

## Снимок режима «Сбалансированный»

Финальная настройка «Лучшая энергоэффективность» эти значения не меняла.

| Параметр | Сеть | Батарея |
|---|---:|---:|
| `PERFEPP` | 90 | 90 |
| `PERFEPP1` | 60 | 60 |
| `PERFEPP2` | 50 | 50 |
| `PROCFREQMAX` | 0 (без лимита Windows) | 2900 МГц |
| `PROCFREQMAX1` | 0 (без лимита Windows) | 3600 МГц |
| `PROCFREQMAX2` | 0 (без лимита Windows) | 0 (без лимита Windows) |
| `PROCTHROTTLEMAX` | 100% | 100% |
| `PROCTHROTTLEMAX1` | 100% | 100% |
| `PROCTHROTTLEMAX2` | 100% | 100% |
| `PERFBOOSTMODE` | 2 — агрессивный | 2 — агрессивный |

## Быстрая проверка текущих значений

```powershell
powercfg /qh 961cc777-2547-4f9d-8174-7d86181b8a7a SUB_PROCESSOR
```

В выводе искать псевдонимы `PERFEPP`, `PERFEPP1`, `PROCFREQMAX` и `PROCFREQMAX1`.

