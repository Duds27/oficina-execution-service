# Testes sem conta AWS: provider mockado e o estado do infra-k8s simulado.
# Rodar com: terraform -chdir=infra init -backend=false && terraform -chdir=infra test

mock_provider "aws" {
  mock_data "aws_iam_policy_document" {
    defaults = {
      json = "{}"
    }
  }
  # A associação do Pod Identity valida o formato do ARN da role.
  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::123456789012:role/oficina-homolog-execution-service"
    }
  }
}

override_data {
  target = data.terraform_remote_state.k8s
  values = {
    outputs = {
      cluster_name = "oficina-homolog"
    }
  }
}

variables {
  environment  = "homolog"
  state_bucket = "bucket-de-teste"
}

run "tabela_single_table_com_ttl_e_pitr" {
  command = plan

  assert {
    condition     = aws_dynamodb_table.execucoes.name == "oficina-homolog-execucoes"
    error_message = "Nome da tabela fora do padrão <projeto>-<ambiente>-execucoes."
  }

  assert {
    condition     = aws_dynamodb_table.execucoes.billing_mode == "PAY_PER_REQUEST"
    error_message = "A tabela deve ser on-demand."
  }

  assert {
    condition     = toset([for i in aws_dynamodb_table.execucoes.global_secondary_index : i.name]) == toset(["GSI1", "GSI2"])
    error_message = "Os índices da fila (GSI1) e do outbox (GSI2) são obrigatórios."
  }

  assert {
    condition     = one(aws_dynamodb_table.execucoes.ttl).attribute_name == "ExpiraEm"
    error_message = "O TTL tem que ser no atributo ExpiraEm, o mesmo que o código grava."
  }

  assert {
    condition     = one(aws_dynamodb_table.execucoes.point_in_time_recovery).enabled
    error_message = "Point-in-time recovery deve estar ligado."
  }

  assert {
    condition     = aws_dynamodb_table.execucoes.deletion_protection_enabled == false
    error_message = "Em homolog a tabela pode ser apagada."
  }
}

run "role_do_pod_por_pod_identity_so_para_a_service_account_do_servico" {
  command = plan

  assert {
    condition = (
      aws_eks_pod_identity_association.execution_service.cluster_name == "oficina-homolog" &&
      aws_eks_pod_identity_association.execution_service.namespace == "oficina-execution" &&
      aws_eks_pod_identity_association.execution_service.service_account == "execution-service"
    )
    error_message = "A associação entrega a role só à ServiceAccount execution-service do namespace oficina-execution, no cluster do infra-k8s."
  }

  assert {
    condition = alltrue([
      for s in data.aws_iam_policy_document.pod_identity_trust.statement :
      toset(s.actions) == toset(["sts:AssumeRole", "sts:TagSession"]) &&
      anytrue([for p in s.principals : p.type == "Service" && contains(p.identifiers, "pods.eks.amazonaws.com")])
    ])
    error_message = "O trust aceita só o serviço do Pod Identity (pods.eks.amazonaws.com), com AssumeRole e TagSession."
  }
}

run "prod_protege_a_tabela_e_o_ecr" {
  command = plan

  variables {
    environment = "prod"
  }

  assert {
    condition     = aws_dynamodb_table.execucoes.deletion_protection_enabled
    error_message = "Em prod a tabela tem proteção contra exclusão."
  }

  assert {
    condition     = aws_ecr_repository.execution_service.force_delete == false
    error_message = "Em prod o ECR não pode ser apagado com imagens."
  }
}

run "ambiente_invalido_e_recusado" {
  command = plan

  variables {
    environment = "dev"
  }

  expect_failures = [var.environment]
}
