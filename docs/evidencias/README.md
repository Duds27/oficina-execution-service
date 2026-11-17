# Evidências de cobertura

Relatório do próprio CI da `main` (job `build-test-quality`), consolidado pelo ReportGenerator a partir de
todos os projetos de teste. O gate do CI exige 80% de linhas, e o SonarCloud tem o quality gate dele.

| | |
| --- | --- |
| Cobertura de linhas | **96.2%** (1044 de 1085 linhas) |
| Cobertura de ramos | 80.3% |
| Commit | [`e0e88cd`](https://github.com/Duds27/oficina-execution-service/commit/e0e88cd) |
| Run do CI | [44054191930](https://github.com/Duds27/oficina-execution-service/actions/runs/44054191930) (artifact `coverage`) |
| SonarCloud | [quality gate e cobertura](https://sonarcloud.io/summary/new_code?id=Duds27_oficina-execution-service) |

![Cobertura de linhas](badge_linecoverage.svg) ![Cobertura de ramos](badge_branchcoverage.svg)

O detalhe por assembly e classe está em [`cobertura-resumo.md`](cobertura-resumo.md). O relatório
completo, mais novo, sai em todo run do CI, no artifact `coverage`.
