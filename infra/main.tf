locals {
  name       = "${var.project}-${var.environment}"
  table_name = "${local.name}-execucoes"

  tags = {
    Project     = var.project
    Environment = var.environment
    Service     = "execution-service"
    ManagedBy   = "terraform"
  }

  # Cluster do oficina-infra-k8s, onde fica a associação do Pod Identity.
  cluster_name = data.terraform_remote_state.k8s.outputs.cluster_name
}

data "terraform_remote_state" "k8s" {
  backend = "s3"
  config = {
    bucket = var.state_bucket
    key    = "infra-k8s/${var.environment}/terraform.tfstate"
    region = var.aws_region
  }
}

# ───────────────────── DynamoDB (single-table) ─────────────────────
# Mesma definição de src/Oficina.Execution.Infrastructure/Persistence/TabelaExecucoes.cs,
# que só cria a tabela no DynamoDB Local.
resource "aws_dynamodb_table" "execucoes" {
  name         = local.table_name
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "PK"
  range_key    = "SK"

  attribute {
    name = "PK"
    type = "S"
  }

  attribute {
    name = "SK"
    type = "S"
  }

  attribute {
    name = "GSI1PK"
    type = "S"
  }

  attribute {
    name = "GSI1SK"
    type = "S"
  }

  attribute {
    name = "GSI2PK"
    type = "S"
  }

  attribute {
    name = "GSI2SK"
    type = "S"
  }

  # Fila por etapa: FILA#{etapa}, ordenada por {entrada na etapa}#{osId}.
  global_secondary_index {
    name            = "GSI1"
    hash_key        = "GSI1PK"
    range_key       = "GSI1SK"
    projection_type = "ALL"
  }

  # Outbox: OUTBOX, ordenado pela data de criação.
  global_secondary_index {
    name            = "GSI2"
    hash_key        = "GSI2PK"
    range_key       = "GSI2SK"
    projection_type = "ALL"
  }

  # Itens de deduplicação (MSG#) expiram em 7 dias.
  ttl {
    attribute_name = "ExpiraEm"
    enabled        = true
  }

  point_in_time_recovery {
    enabled = true
  }

  deletion_protection_enabled = var.environment == "prod"
}

# ───────────────────── IAM: role do pod (Pod Identity) ─────────────────────
# O EKS Pod Identity entrega a role à ServiceAccount, sem provider OIDC no IAM (a SCP da
# organização da conta nega iam:CreateOpenIDConnectProvider, então o IRSA não é possível).
data "aws_iam_policy_document" "pod_identity_trust" {
  statement {
    actions = ["sts:AssumeRole", "sts:TagSession"]

    principals {
      type        = "Service"
      identifiers = ["pods.eks.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "execution_service" {
  name               = "${local.name}-execution-service"
  assume_role_policy = data.aws_iam_policy_document.pod_identity_trust.json
}

# Só a ServiceAccount do serviço, no namespace dele, recebe a role. A associação tem que existir
# antes do pod nascer: é na criação que o EKS injeta as variáveis de credencial.
resource "aws_eks_pod_identity_association" "execution_service" {
  cluster_name    = local.cluster_name
  namespace       = var.namespace
  service_account = var.service_account
  role_arn        = aws_iam_role.execution_service.arn

  tags = local.tags
}

# Menor privilégio: só a tabela e os índices dela, só as ações que o código usa.
# Não existe ação IAM "TransactWriteItems": a transação é autorizada item a item,
# pela ação de cada um (PutItem, e ConditionCheckItem se o código passar a usar).
data "aws_iam_policy_document" "dynamodb" {
  statement {
    sid = "ExecucoesTabelaEIndices"
    actions = [
      "dynamodb:GetItem",
      "dynamodb:Query",
      "dynamodb:PutItem",
      "dynamodb:UpdateItem",
      "dynamodb:DeleteItem",
      "dynamodb:ConditionCheckItem",
    ]
    resources = [
      aws_dynamodb_table.execucoes.arn,
      "${aws_dynamodb_table.execucoes.arn}/index/*",
    ]
  }
}

resource "aws_iam_role_policy" "dynamodb" {
  name   = "${local.name}-execution-dynamodb"
  role   = aws_iam_role.execution_service.id
  policy = data.aws_iam_policy_document.dynamodb.json
}

# ───────────────────────────── ECR ─────────────────────────────
resource "aws_ecr_repository" "execution_service" {
  name                 = "${local.name}-execution-service"
  image_tag_mutability = "MUTABLE"
  force_delete         = var.environment != "prod"

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "execution_service" {
  repository = aws_ecr_repository.execution_service.name
  policy = jsonencode({
    rules = [{
      rulePriority = 1
      description  = "Mantém as 20 imagens mais recentes"
      selection = {
        tagStatus   = "any"
        countType   = "imageCountMoreThan"
        countNumber = 20
      }
      action = { type = "expire" }
    }]
  })
}
